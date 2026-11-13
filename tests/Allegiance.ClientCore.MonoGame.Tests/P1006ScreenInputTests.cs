using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P1006ScreenInputTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private static object? Call(Game1 game, string name, params object?[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);
    private static void State(Game1 game, string value)
    {
        var field = typeof(Game1).GetField("_state", Hidden)!;
        field.SetValue(game, Enum.Parse(field.FieldType, value));
    }
    private static Game1 Headless(ConnectionSession session, HudStyle style)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_autoPlan", new AutoProofPlan());
        Set(game, "_hangarLaunch", new HangarLaunchSequence());
        Set(game, "_hangarLaunchAnimation", new HangarLaunchAnimation());
        Set(game, "_investmentTabs", new InvestmentTabsInput());
        Set(game, "_investmentFunding", new InvestmentFundingInput());
        Set(game, "_consolePressEdges", new ActionPressEdges());
        Set(game, "_pilotBuf", new StringBuilder());
        Set(game, "_directBuf", new StringBuilder());
        Set(game, "_joinLinkEvents", new ConcurrentQueue<string>());
        Set(game, "_identityScreen", new IdentityScreenModel());
        Set(game, "_introProofDelayMs", -1L);
        Set(game, "_introEditingPilot", true);
        Set(game, "_dockedActive", true);
        Set(game, "_hangarOnly", true);
        State(game, "Flying");
        return game;
    }
    private static void OwnShip(ConnectionSession session)
    {
        session.World.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0,
            Allegiance.Core.Collision.Vector3.Zero, Orientation.Identity, Allegiance.Core.Collision.Vector3.Zero,
            [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        session.World.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "P1006 pilot" });
        session.World.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 42, ParentId = -1,
            State = ShipStatusMessage.StateDocked });
    }
    private static MouseState Pointer(HangarAction action, bool pressed)
    {
        var rect = HangarScreen.Rect(800, 480, action);
        return new MouseState(rect.Center.X, rect.Center.Y, 0,
            pressed ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }

    public static IEnumerable<object[]> HiddenClicks() =>
        from style in new[] { HudStyle.Classic, HudStyle.Modern }
        from action in HangarControls.Actions(true)
        from docked in new[] { true, false }
        select new object[] { style, action, docked };

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Briefing_closes_on_authoritative_start_even_without_a_local_countdown(HudStyle style)
    {
        using var session = new ConnectionSession(); OwnShip(session);
        var game = Headless(session, style);
        session.World.Apply(new SetBriefingText { Text = "Home sector", FGenerateCivBriefing = true });
        Assert.Null(session.World.CountdownStartLocalMs);
        Assert.False((bool)Call(game, "IsBriefingVisible", new object?[] { null })!);
        session.World.Apply(new MissionDef { Stage = (int)MissionStage.Starting });
        Assert.True((bool)Call(game, "IsBriefingVisible", new object?[] { null })!);
        session.World.Apply(new MissionDef { Stage = (int)MissionStage.Started });
        Assert.False((bool)Call(game, "IsBriefingVisible", new object?[] { null })!);
        // The native briefing is absent in the other lobby/end-game stages too.
        session.World.Apply(new MissionDef { Stage = (int)MissionStage.NotStarted });
        Assert.False((bool)Call(game, "IsBriefingVisible", new object?[] { null })!);
        session.World.Apply(new MissionDef { Stage = (int)MissionStage.Over });
        Assert.False((bool)Call(game, "IsBriefingVisible", new object?[] { null })!);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Authoritative_launch_retires_hangar_even_with_investment_open(HudStyle style)
    {
        using var session = new ConnectionSession(); OwnShip(session);
        var game = Headless(session, style);
        Set(game, "_shopModel", new ShopModel(ShopCatalog.Empty, 7, Array.Empty<ExpandedPartData>()));
        Set(game, "_investActive", true);
        Set(game, "_teleportActive", true);
        Call(game, "ClearUndockedHangar");
        Assert.True(Get<bool>(game, "_dockedActive"));
        session.World.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = -1,
            ParentId = -1, State = ShipStatusMessage.StateFlying });
        Call(game, "ClearUndockedHangar");
        Assert.False(Get<bool>(game, "_dockedActive"));
        Assert.Null(Get<ShopModel?>(game, "_shopModel"));
        Assert.False(Get<bool>(game, "_teleportActive"));
        // Native permits investment in space; only the docked screen retires.
        Assert.True(Get<bool>(game, "_investActive"));
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Splash_skip_and_intro_back_are_consumed_before_the_global_menu(HudStyle style)
    {
        using var session = new ConnectionSession();
        var game = Headless(session, style); State(game, "Splash");
        Set(game, "_splashStartSeconds", 1e9);
        Assert.True((bool)Call(game, "HandleIntroScreenInput", new KeyboardState(Keys.Escape))!);
        Assert.Equal("Intro", Get<object>(game, "_state").ToString());
        Assert.False(Get<bool>(game, "_mainMenuVisible"));
        Assert.True(Get<bool>(game, "_escWasDown"));

        var view = typeof(Game1).GetField("_introView", Hidden)!;
        view.SetValue(game, Enum.Parse(view.FieldType, "Credits"));
        Assert.True((bool)Call(game, "HandleIntroScreenInput", new KeyboardState(Keys.Escape))!);
        Assert.Equal("Main", Get<object>(game, "_introView").ToString());
        Assert.False(Get<bool>(game, "_mainMenuVisible"));
        // Root intro remains eligible for the explicit native Escape popup.
        Assert.False((bool)Call(game, "HandleIntroScreenInput", new KeyboardState())!);
    }

    [Theory]
    [MemberData(nameof(HiddenClicks))]
    public void Command_view_ignores_every_invisible_hangar_button(HudStyle style, HangarAction action, bool docked)
    {
        using var session = new ConnectionSession(); OwnShip(session);
        var game = Headless(session, style);
        Set(game, "_cameraMode", CameraMode.Command);
        Set(game, "_dockedActive", docked);
        Set(game, "_hangarScriptMouse", Pointer(action, true));
        // Production pointer path, including click edge detection. No graphics
        // device is needed: an invisible screen must never reach hit testing.
        Assert.False((bool)Call(game, "UpdateHangarControls", new KeyboardState(), false)!);
        Assert.Equal(CameraMode.Command, Get<CameraMode>(game, "_cameraMode"));
        Assert.True(Get<bool>(game, "_hangarOnly"));
        Assert.False(Get<bool>(game, "_investActive"));
        Assert.False(Get<bool>(game, "_teleportActive"));
        Assert.False(Get<bool>(game, "_teamPaneVisible"));
        Assert.False(Get<bool>(game, "_helpVisible"));
        Assert.False(Get<bool>(game, "_hangarQuitConfirm"));
        Assert.False(Get<bool>(game, "_launchSent"));
        Assert.Equal(HangarAction.None, Get<HangarAction>(game, "_hangarHover"));
        Assert.True(Get<bool>(game, "_hangarMouseDown"));
        Set(game, "_hangarScriptMouse", Pointer(action, false));
        Call(game, "UpdateHangarControls", new KeyboardState(), false);
        Assert.False(Get<bool>(game, "_hangarMouseDown"));
    }

    [Theory]
    [InlineData(HudStyle.Classic, CameraMode.Command)] [InlineData(HudStyle.Modern, CameraMode.Command)]
    [InlineData(HudStyle.Classic, CameraMode.DroneFollow)] [InlineData(HudStyle.Modern, CameraMode.DroneFollow)]
    [InlineData(HudStyle.Classic, CameraMode.DroneCockpit)] [InlineData(HudStyle.Modern, CameraMode.DroneCockpit)]
    public void Command_view_keyboard_cannot_navigate_buy_or_launch_the_hidden_shop(HudStyle style, CameraMode camera)
    {
        using var session = new ConnectionSession(); OwnShip(session);
        var game = Headless(session, style);
        Set(game, "_cameraMode", camera);
        var hull = new DataHullTypeIGC
        {
            HullId = 7, SuccessorHullId = -1, Name = "Scout", Hardpoints = [],
            PmEquipment = [], PreferredPartsTypes = [], TtbmRequired = [], TtbmEffects = [],
        };
        var catalog = new ShopCatalog([hull, hull with { HullId = 8, Name = "Fighter" }], []);
        Set(game, "_shopModel", new ShopModel(catalog, 7, Array.Empty<ExpandedPartData>()));
        Set(game, "_hangarScriptMouse", Pointer(HangarAction.Loadout, false));
        var shop = Get<ShopModel>(game, "_shopModel");
        Assert.Equal(2, shop.RowCount); // hidden navigation would change a real selection
        // A retained loadout is also invisible to Escape in every command
        // camera. The renderer and Escape chain use this same view state.
        Set(game, "_hangarOnly", false);
        Assert.False((bool)typeof(Game1).GetProperty("DockedLoadoutView", Hidden)!.GetValue(game)!);
        var escape = MainMenuRules.Escape(false, false, false, false, false, false,
            mainMenuPressed: true, loadout: (bool)typeof(Game1).GetProperty("DockedLoadoutView", Hidden)!.GetValue(game)!);
        Assert.Equal(EscapeUiAction.ToggleMainMenu, escape);
        Set(game, "_hangarOnly", true);
        foreach (var key in new[] { Keys.Down, Keys.Enter, Keys.Space, Keys.Tab, Keys.E, Keys.L, Keys.U, Keys.D0 })
        {
            Assert.False((bool)Call(game, "HandleDockedScreen", new KeyboardState(key), false)!);
            Assert.False((bool)Call(game, "HandleDockedScreen", new KeyboardState(), false)!);
            Assert.Equal(camera, Get<CameraMode>(game, "_cameraMode"));
            Assert.Equal(7, shop.EffectiveHullId);
            Assert.Equal(0, shop.Cursor);
            Assert.Equal(ShopTab.Hulls, shop.Tab);
            Assert.Equal(ShopModel.ShopPage.HullList, shop.Page);
            Assert.Empty(shop.ChosenParts);
            Assert.False(Get<bool>(game, "_launchSent"));
            Assert.False(Get<bool>(game, "_teleportActive"));
        }
        // Native TK_ConModeInventory is an explicit switch, not hidden shop
        // navigation. It remains legal while docked in command view.
        Call(game, "ApplyConsoleMode", "ConModeInventory", false);
        Assert.Equal(CameraMode.Cockpit, Get<CameraMode>(game, "_cameraMode"));
        Assert.False(Get<bool>(game, "_hangarOnly"));
        Assert.True((bool)typeof(Game1).GetProperty("DockedLoadoutView", Hidden)!.GetValue(game)!);
        // The same action from a command view entered FROM loadout still
        // opens loadout, rather than treating the hidden old screen as active.
        Set(game, "_cameraMode", CameraMode.Command);
        Set(game, "_investActive", true);
        Call(game, "ApplyConsoleMode", "ConModeInventory", false);
        Assert.Equal(CameraMode.Cockpit, Get<CameraMode>(game, "_cameraMode"));
        Assert.False(Get<bool>(game, "_hangarOnly"));
        Assert.False(Get<bool>(game, "_investActive"));
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Loadout_screen_and_covering_panes_disable_hangar_hit_testing(HudStyle style)
    {
        using var session = new ConnectionSession();
        var game = Headless(session, style);
        Set(game, "_hangarScriptMouse", Pointer(HangarAction.Exit, true));
        Set(game, "_hangarOnly", false);
        Call(game, "UpdateHangarControls", new KeyboardState(), false);
        Assert.False(Get<bool>(game, "_hangarQuitConfirm"));
        Set(game, "_hangarOnly", true);
        foreach (var field in new[] { "_investActive", "_teleportActive", "_teamPaneVisible", "_mainMenuVisible",
                     "_helpVisible", "_keyBindVisible", "_graphicsSettingsVisible", "_creditsVisible", "_playerPingsVisible" })
        {
            Set(game, field, true);
            Call(game, "UpdateHangarControls", new KeyboardState(), false);
            Assert.False(Get<bool>(game, "_hangarQuitConfirm"));
            Set(game, field, false);
        }
    }

    [Theory]
    [InlineData(HudStyle.Classic, true)] [InlineData(HudStyle.Modern, true)]
    [InlineData(HudStyle.Classic, false)] [InlineData(HudStyle.Modern, false)]
    public void Intro_popup_owns_navigation_activation_and_typed_text_including_its_closing_frame(HudStyle style, bool open)
    {
        using var session = new ConnectionSession();
        var game = Headless(session, style); State(game, "Intro");
        Set(game, "_mainMenuVisible", open);
        Set(game, "_menuConsumedFrame", !open);
        Call(game, "UpdateIntro", new KeyboardState(Keys.Down, Keys.Enter, Keys.Tab));
        Call(game, "OnTextInput", null, new TextInputEventArgs('x', Keys.X));
        Assert.Equal(0, Get<int>(game, "_introIndex"));
        Assert.Empty(Get<StringBuilder>(game, "_pilotBuf").ToString());
        Assert.True(Get<bool>(game, "_introEditingPilot"));
        Assert.True(Get<bool>(game, "_iDown"));
        Assert.True(Get<bool>(game, "_iEnter"));
        Assert.True(Get<bool>(game, "_iTab"));
        Assert.Equal("Intro", Get<object>(game, "_state").ToString());
        // Key release is also observed while covered, so the next real press
        // is available to the restored intro.
        Call(game, "UpdateIntro", new KeyboardState());
        Assert.False(Get<bool>(game, "_iDown"));
        Assert.False(Get<bool>(game, "_iEnter"));
    }
}
