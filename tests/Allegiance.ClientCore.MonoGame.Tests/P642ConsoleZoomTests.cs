using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P642ConsoleZoomTests
{
    [Theory]
    [InlineData(TeamConsoleSize.Hidden, "TeamPane", TeamConsoleSize.Expanded)]
    [InlineData(TeamConsoleSize.Mini, "TeamPane", TeamConsoleSize.Expanded)]
    [InlineData(TeamConsoleSize.Expanded, "TeamPane", TeamConsoleSize.Hidden)]
    [InlineData(TeamConsoleSize.Hidden, "ConModeMiniTeam", TeamConsoleSize.Mini)]
    [InlineData(TeamConsoleSize.Expanded, "ConModeMiniTeam", TeamConsoleSize.Mini)]
    [InlineData(TeamConsoleSize.Mini, "ConModeMiniTeam", TeamConsoleSize.Hidden)]
    public void Team_size_switches_and_banishes_game_state_preserving_inventory_and_nav(TeamConsoleSize initial, string action, TeamConsoleSize expected)
    {
        var result = ConsoleModeRules.Switch(new(true, true, true, initial), action);
        Assert.Equal(expected, result.Team);
        Assert.False(result.GameState);
        Assert.True(result.Inventory);
        Assert.True(result.Nav);
    }
    [Fact]
    public void Nav_and_inventory_toggle_independently_game_state_banishes_roster()
    {
        var state = new ConsolePaneState(false, false, false, TeamConsoleSize.Expanded);
        state = ConsoleModeRules.Switch(state, "ConModeNav");
        state = ConsoleModeRules.Switch(state, "ConModeInventory");
        Assert.Equal(new(false, true, true, TeamConsoleSize.Expanded), state);
        state = ConsoleModeRules.Switch(state, "ConModeGameState");
        Assert.Equal(new(true, true, true, TeamConsoleSize.Hidden), state);
        state = ConsoleModeRules.Switch(state, "ConModeGameState");
        Assert.False(state.GameState);
    }
    [Theory]
    [InlineData("ConModeCombat")]
    [InlineData("ViewCommand")]
    [InlineData("ViewExternal")]
    public void Combat_command_external_banish_modal_roster_and_game_state(string action)
    {
        var result = ConsoleModeRules.Switch(new(true, true, true, TeamConsoleSize.Expanded), action);
        Assert.Equal(new(false, true, true, TeamConsoleSize.Hidden), result);
    }
    [Fact]
    public void Cockpit_zoom_changes_radians_external_zoom_scales_distance_and_both_clamp()
    {
        var zoom = new FlightCameraZoom();
        float fov = zoom.FovRadians;
        zoom.Step(.1f, true, false, true);
        Assert.Equal(fov - .1f, zoom.FovRadians, 5);
        Assert.Equal(500, zoom.ExternalDistance);
        zoom.Step(100, true, false, true);
        Assert.Equal(MathHelper.ToRadians(5), zoom.FovRadians);
        zoom.Step(100, false, true, true);
        Assert.Equal(MathHelper.ToRadians(50), zoom.FovRadians);
        zoom.Step(.1f, true, false, false);
        Assert.Equal(450, zoom.ExternalDistance);
        zoom.Step(.1f, false, true, false);
        Assert.Equal(495, zoom.ExternalDistance);
        zoom.Step(100, false, true, false);
        Assert.Equal(6000, zoom.ExternalDistance);
        zoom.Step(100, true, false, false);
        Assert.Equal(100, zoom.ExternalDistance);
    }
    [Fact]
    public void Native_zoom_priorities_and_negative_dt_are_stable()
    {
        var zoom = new FlightCameraZoom();
        zoom.Step(.1f, true, true, true); // cockpit in wins
        Assert.Equal(MathHelper.ToRadians(50) - .1f, zoom.FovRadians, 5);
        zoom.Step(.1f, true, true, false); // external out wins
        Assert.Equal(550, zoom.ExternalDistance);
        zoom.Step(-1, false, true, false);
        Assert.Equal(550, zoom.ExternalDistance);
    }
    [Fact]
    public void Zoom_is_visible_in_projection_and_external_placement()
    {
        var camera = new CockpitCamera();
        var (_, before) = camera.Matrices(Vector3.Zero, Matrix.Identity, 1, out _);
        camera.FieldOfViewRadians = MathHelper.ToRadians(10);
        var (_, after) = camera.Matrices(Vector3.Zero, Matrix.Identity, 1, out _);
        Assert.True(after.M22 > before.M22 * 4);
        var chase = new ChaseCamera(350, 90, turnSmoothingTauSeconds: 0) { DistanceOverride = 1000 };
        chase.Matrices(Vector3.Zero, Matrix.Identity, 1, .1f, out var distant, 20);
        chase.DistanceOverride = 100;
        chase.Matrices(Vector3.Zero, Matrix.Identity, 1, .1f, out var near, 20);
        Assert.True(distant.Length() > near.Length() * 9);
        Assert.Equal(205, ExternalCameras.GenericExternal(Vector3.Zero, Vector3.UnitZ, 100, 100).Eye.Z);
    }
    [Fact]
    public void Grid_preference_roundtrips_and_corrupt_file_defaults_visible()
    {
        string directory = Path.Combine(Path.GetTempPath(), "p642-" + Guid.NewGuid());
        string path = Path.Combine(directory, "console-settings.json");
        try
        {
            Assert.True(CommandGridPreference.Load(path));
            CommandGridPreference.Save(path, false);
            Assert.False(CommandGridPreference.Load(path));
            CommandGridPreference.Save(path, true);
            Assert.True(CommandGridPreference.Load(path));
            File.WriteAllText(path, "malformed");
            Assert.True(CommandGridPreference.Load(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("ConModeCombat")]
    [InlineData("ConModeGameState")]
    [InlineData("ConModeNav")]
    [InlineData("ConModeInventory")]
    [InlineData("ConModeMiniTeam")]
    [InlineData("TeamPane")]
    [InlineData("ViewExternal")]
    [InlineData("ToggleGrid")]
    [InlineData("OccupyNextTurret")]
    [InlineData("MainMenu")]
    [InlineData("ZoomIn")]
    [InlineData("ZoomOut")]
    public void Visible_commands_are_rebindable_and_help_names_native_identity(string action)
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var definition = Assert.Single(map.Definitions, d => d.Name == action);
        Assert.Contains("TK_", definition.Description);
        map.SetBinding(action, new(Keys.F24, InputModifiers.None), force: true);
        Assert.True(map.IsDown(new KeyboardState(Keys.F24), action));
        if (definition.Default.Key != Keys.None) Assert.False(map.IsDown(new KeyboardState(definition.Default.Key), action));
    }
    [Fact]
    public void Every_TK_action_exposes_the_native_MDL_default_including_unbound_commands()
    {
        var native = DefaultInputMap.ParseKeyMap(DefaultInputMap.EmbeddedText);
        foreach (var definition in InputActionCatalog.Definitions)
        {
            var identities = System.Text.RegularExpressions.Regex.Matches(definition.Description, @"TK_\w+");
            if (identities.Count == 0) continue;
            string identity = identities[^1].Value;
            var expected = native.Where(n => n.TkName == identity).Select(DefaultInputMap.ToChord).FirstOrDefault();
            Assert.True(expected == definition.NativeDefault,
                $"{definition.Name} ({identity}): expected native {expected?.Label ?? "unbound"}, actual {definition.NativeDefault?.Label ?? "unbound"}");
        }
    }

    [Fact]
    public void Press_edges_consume_composition_and_prime_held_keys_across_flight_entry()
    {
        var edges = new ActionPressEdges();
        Assert.False(edges.Observe("TargetWarp", true, suppressed: true));
        Assert.False(edges.Observe("TargetWarp", true));
        Assert.False(edges.Observe("TargetWarp", false));
        Assert.True(edges.Observe("TargetWarp", true));
        Assert.True(edges.Observe("ConModeInventory", true));
        Assert.False(edges.Observe("ConModeInventory", true));
        edges.Prime(new[] { ("TargetWarp", true), ("TargetFlag", false) });
        Assert.False(edges.Observe("TargetWarp", true));
        Assert.True(edges.Observe("TargetFlag", true));
    }
    [Theory]
    [InlineData(true, true, false, false, false, false, EscapeUiAction.Briefing)]
    [InlineData(false, true, true, false, false, false, EscapeUiAction.Chat)]
    [InlineData(false, false, true, false, false, false, EscapeUiAction.QuickChat)]
    [InlineData(false, false, false, true, false, false, EscapeUiAction.Controls)]
    [InlineData(false, false, false, false, true, false, EscapeUiAction.Help)]
    [InlineData(false, false, false, false, false, true, EscapeUiAction.Credits)]
    [InlineData(false, false, false, false, false, false, EscapeUiAction.ToggleMainMenu)]
    public void Main_menu_cancels_composition_first_then_opens_a_menu_without_quitting(bool briefing, bool chat, bool quick, bool controls, bool help, bool credits, EscapeUiAction expected)
    {
        Assert.Equal(expected, MainMenuRules.Escape(briefing, chat, quick, controls, help, credits, true));
        Assert.Equal(EscapeUiAction.None, MainMenuRules.Escape(false, false, false, false, false, false, false));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, false)]
    public void Docked_inventory_switches_loadout_hangar_and_closes_banishable_panes(bool hangar, bool panes, bool expectedHangar, bool expectedClose)
    {
        Assert.Equal((expectedHangar, expectedClose), ConsoleModeRules.DockedInventory(hangar, panes));
    }
    [Fact]
    public void Main_menu_is_inert_during_override_or_connection_lockdown()
    {
        Assert.Equal(EscapeUiAction.None, MainMenuRules.Escape(false, false, false, false, false, false, true, overrideView: true));
    }
    [Fact]
    public void Mini_team_uses_only_own_side_and_preserves_self_highlight_and_money()
    {
        Allegiance.Core.Wire.ScoreRosterEntry[] entries = [new("pilot", 0, 2, 1, 0, 0, 0), new("enemy", 1, 5, 0, 0, 0, 0)];
        var expanded = TeamPaneLayout.Build(entries, [], new Dictionary<string, TeamPaneLayout.ShipBrief>(), null, 123, null, false, "pilot");
        var mini = TeamPaneLayout.Compact(expanded, entries, 0);
        var pilot = Assert.Single(mini, l => l.Kind == TeamPaneLayout.LineKind.Pilot);
        Assert.True(pilot.Highlight);
        Assert.Contains("pilot", pilot.Text);
        Assert.Contains(mini, l => l.Kind == TeamPaneLayout.LineKind.Money && l.Text.Contains("123"));
        Assert.DoesNotContain(mini, l => l.Text.Contains("enemy"));
    }

    [Fact]
    public void New_command_defaults_do_not_collide_with_existing_actions()
    {
        var names = Game1.RemainderTargetActions.Select(a => a.Id).Concat(new[] { "ConModeCombat", "ConModeGameState",
            "ConModeNav", "ConModeInventory", "ConModeMiniTeam", "ViewExternal", "ToggleGrid", "OccupyNextTurret", "MainMenu" }).ToHashSet();
        names.ExceptWith(new[] { "Target", "TargetEnemyNearest", "TargetFriendlyNearest", "TargetEnemyBaseNearest", "TargetFriendlyBaseNearest" });
        foreach (var action in InputActionCatalog.Definitions.Where(a => names.Contains(a.Name) && a.Default.Key != Keys.None))
        {
            var conflicts = InputActionCatalog.Definitions.Where(other => other.Name != action.Name && other.Default == action.Default)
                .Select(other => other.Name).ToArray();
            Assert.Equal(action.Name == "MainMenu" ? new[] { "CancelChat", "LobbyClearFilter" } : Array.Empty<string>(), conflicts);
        }
    }

    [Fact]
    public void Team_pane_caption_accepts_live_rebound_keys()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("TeamPane", new(Keys.F24, InputModifiers.None), force: true);
        var lines = TeamPaneLayout.Build([], [], new Dictionary<string, TeamPaneLayout.ShipBrief>(),
            null, null, null, false, null, controlsHint: map.BindingOf("TeamPane").Label + " TOGGLE");
        Assert.Contains(lines, l => l.Kind == TeamPaneLayout.LineKind.Hint && l.Text == "F24 TOGGLE");
    }

    [Fact]
    public void Proof_plan_exercises_production_warp_and_console_commands() =>
        Assert.True(AutoProofPlan.Parse(["--auto-parity-commands"], out _).AutoParityCommands);
}
