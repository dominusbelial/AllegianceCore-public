using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using Kind = Allegiance.ClientCore.MonoGame.Rendering.LoadoutV2Layout.Kind;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1015 — which loadout screen owns the input, through the production
/// <c>Game1.HandleDockedScreen</c> frame: with <c>visuals.hangar.loadoutV2</c>
/// off the old shop keys run exactly as before and no V2 router exists; with it
/// on only the V2 router acts, and not at all under chat, a popup or the
/// command screen (P1006).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P1015LoadoutScreenRoutingTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private static object? Raw(Game1 game, string name) => typeof(Game1).GetField(name, Hidden)!.GetValue(game);
    private static object? Call(Game1 game, string name, params object?[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);
    private static bool Shown(Game1 game) => (bool)typeof(Game1).GetProperty("LoadoutV2Shown", Hidden)!.GetValue(game)!;

    private sealed class CountingSink : IUiSoundSink
    {
        public int MouseOver;
        public void PlayMouseOver() => MouseOver++;
        public void PlayMapClick() { }
        public void PlayError() { }
        public void PlayPositiveClick() { }
        public void PlayMountPart() { }
        public void PlayVoice(string assetStem) { }
        public void PlayPersonalJump() { }
    }

    private static Game1 Docked(ConnectionSession session, HudStyle style, bool flag, ShopModel shop)
    {
        session.World.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0,
            Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        session.World.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "P1015 pilot" });
        session.World.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 42, ParentId = -1,
            State = ShipStatusMessage.StateDocked });
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_autoPlan", new AutoProofPlan());
        Set(game, "_hangarLaunch", new HangarLaunchSequence());
        Set(game, "_hangarLaunchAnimation", new HangarLaunchAnimation());
        Set(game, "_hangarEdges", new ActionPressEdges());
        Set(game, "_investmentTabs", new InvestmentTabsInput());
        Set(game, "_investmentFunding", new InvestmentFundingInput());
        Set(game, "_consolePressEdges", new ActionPressEdges());
        Set(game, "_pilotBuf", new StringBuilder());
        Set(game, "_directBuf", new StringBuilder());
        Set(game, "_joinLinkEvents", new ConcurrentQueue<string>());
        Set(game, "_loadoutDigitWasDown", new bool[10]);
        Set(game, "_dockedActive", true);
        Set(game, "_hangarOnly", false);            // the loadout view
        Set(game, "_cameraMode", CameraMode.Cockpit);
        Set(game, "_touchChecked", true);           // no touch panel in the test host
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        var visuals = new VisualOptions();
        visuals.SetToggle(VisualFlags.LoadoutV2Key, flag);
        Set(game, "_visuals", visuals);
        Set(game, "_shopModel", shop);
        Set(game, "_hangarScriptMouse", Pointer(1, 1, false));
        return game;
    }

    private static MouseState Pointer(float x, float y, bool pressed, int wheel = 0) => new((int)x, (int)y, wheel,
        pressed ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released,
        ButtonState.Released, ButtonState.Released, ButtonState.Released);

    /// <summary>What Game1.DrawLoadoutV2 leaves behind for the next update.</summary>
    private static (LoadoutV2Model Model, Func<LoadoutV2Layout> Draw) Drawn(Game1 game, ShopModel shop,
        LoadoutV2Model? model = null)
    {
        model ??= new LoadoutV2Model(shop);
        Set(game, "_loadoutV2Model", model);
        LoadoutV2Layout Draw()
        {
            var router = (LoadoutV2Input)typeof(Game1).GetProperty("LoadoutV2InputRouter", Hidden)!.GetValue(game)!;
            var layout = LoadoutV2Presentation.Build(model, null, null, router).Layout(1280, 720, 1f, router);
            Set(game, "_loadoutV2Layout", layout);
            return layout;
        }
        return (model, Draw);
    }

    private static void Frame(Game1 game, params Keys[] keys)
    {
        Assert.True((bool)Call(game, "HandleDockedScreen", new KeyboardState(keys), false)!);
        Assert.True((bool)Call(game, "HandleDockedScreen", new KeyboardState(), false)!);
    }

    private static ShopModel TwoHulls()
    {
        var hull = new DataHullTypeIGC
        {
            HullId = 7, SuccessorHullId = -1, Name = "Scout", Hardpoints = [],
            PmEquipment = [], PreferredPartsTypes = [], TtbmRequired = [], TtbmEffects = [],
        };
        return new ShopModel(new ShopCatalog([hull, hull with { HullId = 8, Name = "Fighter" }], []),
            7, Array.Empty<ExpandedPartData>());
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Flag_off_the_old_shop_keys_run_as_before_and_no_v2_router_is_created(HudStyle style)
    {
        using var session = new ConnectionSession();
        var shop = TwoHulls();
        var game = Docked(session, style, flag: false, shop);
        // Even a V2 model/layout left over from an earlier flag-on frame is inert.
        Drawn(game, shop);
        Assert.False(Shown(game));
        // The old hover reads the real viewport (no device here); chat focus is
        // its own documented off switch and leaves the key path under test alone.
        Set(game, "_chatActive", true);

        Frame(game, Keys.Down);
        Assert.Equal(1, shop.Cursor);                       // old cursor
        Frame(game, Keys.Space);
        Assert.Equal(8, shop.EffectiveHullId);              // old select
        Frame(game, Keys.Q);
        Assert.Equal(7, shop.EffectiveHullId);              // old hull cycle
        Frame(game, Keys.Tab);
        Assert.Equal(ShopTab.Parts, shop.Tab);              // old tab switch
        Frame(game, Keys.B);
        Assert.Equal(ShopTab.Hulls, shop.Tab);              // old back
        Assert.False(Get<bool>(game, "_hangarOnly"));
        // The V2-only keys mean nothing here (a plain digit is still native's
        // custom-loadout restore, which keeps the hull).
        Frame(game, Keys.D2);
        Frame(game, Keys.D);
        Frame(game, Keys.Right);
        Assert.Equal((7, ShopTab.Hulls, ShopModel.ShopPage.HullList), (shop.EffectiveHullId, shop.Tab, shop.Page));
        Assert.Null(Raw(game, "_loadoutV2InputState"));
        Frame(game, Keys.L);
        Assert.True(Get<bool>(game, "_launchSent"));        // old launch
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Flag_on_only_the_v2_router_acts_and_the_old_cursor_tab_and_bands_are_dead(HudStyle style)
    {
        using var session = new ConnectionSession();
        var shop = TwoHulls();
        var game = Docked(session, style, flag: true, shop);
        var sink = new CountingSink();
        typeof(Game1).GetField("_uiSoundOverride", Hidden | BindingFlags.Public)!.SetValue(game, sink);
        var (model, draw) = Drawn(game, shop);
        var layout = draw();
        Assert.True(Shown(game));

        // Old verbs with no V2 meaning change nothing in the old shop.
        Frame(game, Keys.Down);
        Assert.Equal(0, shop.Cursor);
        Assert.Equal(ShopModel.ShopPage.HullList, shop.Page);
        var router = Get<LoadoutV2Input>(game, "_loadoutV2InputState");
        Assert.Equal(1, router.HullFocus);
        Frame(game, Keys.Space);
        Assert.Equal(8, shop.EffectiveHullId);
        Frame(game, Keys.Tab);
        Assert.Equal(ShopTab.Hulls, shop.Tab);
        Assert.Equal(LoadoutV2Column.Slots, router.Column);
        Frame(game, Keys.D1);
        Assert.Equal(LoadoutV2Column.Hulls, router.Column);
        Frame(game, Keys.Left);
        Assert.Equal(7, shop.EffectiveHullId);

        // The mouse: hovering the old screen's row band (centre, 30% down) moves
        // no shop cursor; the V2 hull row under the pointer is what a click hits.
        layout = draw();
        Set(game, "_hangarScriptMouse", Pointer(640, 0.30f * 720 + 80, false));
        Frame(game);
        Assert.Equal(0, shop.Cursor);
        var row = layout.Elements.First(e => e.Kind == Kind.Hull && e.Index == 1);
        Set(game, "_hangarScriptMouse", Pointer(row.Bounds.CenterX, row.Bounds.CenterY, false));
        Frame(game);
        Assert.Equal(1, sink.MouseOver);                    // native slot MouseEnter cue
        Assert.Equal(7, shop.EffectiveHullId);
        Set(game, "_hangarScriptMouse", Pointer(row.Bounds.CenterX, row.Bounds.CenterY, true));
        Frame(game);
        Assert.Equal(8, shop.EffectiveHullId);
        Assert.Equal(0, shop.Cursor - shop.HullCursor);     // the shop's own bookkeeping only
        Set(game, "_hangarScriptMouse", Pointer(1, 1, false));
        Frame(game);

        // Back leaves to the hangar like Escape; then the hangar's old keys are
        // live again and the V2 router is gated.
        Assert.True((bool)Call(game, "HandleDockedScreen", new KeyboardState(Keys.B), false)!);
        Assert.True(Get<bool>(game, "_hangarOnly"));
        Assert.False(Shown(game));
        Set(game, "_hangarOnly", false);
        Frame(game);

        // Launch goes down the shared LaunchFromHangar path.
        var launch = layout.Elements.First(e => e.Kind == Kind.Launch);
        Set(game, "_hangarScriptMouse", Pointer(launch.Bounds.CenterX, launch.Bounds.CenterY, true));
        Assert.False(Get<bool>(game, "_launchSent"));
        Frame(game);
        Assert.True(Get<bool>(game, "_launchSent"));
        _ = model;
    }

    public static IEnumerable<object[]> Gates() =>
        from style in new[] { HudStyle.Classic, HudStyle.Modern }
        from gate in new[] { "_chatActive", "_qcMenuOpen", "_helpVisible", "_mainMenuVisible", "_keyBindVisible",
            "_graphicsSettingsVisible", "_creditsVisible", "_playerPingsVisible", "_teamPaneVisible",
            "_hangarQuitConfirm", "_trainingQuitConfirm", "_launchSent", "_investActive", "_teleportActive",
            "composing", "command" }
        select new object[] { style, gate };

    [Theory, MemberData(nameof(Gates))]
    public void Nothing_on_the_v2_screen_reacts_under_chat_a_popup_or_the_command_screen(HudStyle style, string gate)
    {
        using var session = new ConnectionSession();
        var shop = TwoHulls();
        var game = Docked(session, style, flag: true, shop);
        var (_, draw) = Drawn(game, shop);
        var layout = draw();
        var row = layout.Elements.First(e => e.Kind == Kind.Hull && e.Index == 1);
        var over = Pointer(row.Bounds.CenterX, row.Bounds.CenterY, true, wheel: -240);
        var live = typeof(Game1).GetMethod("LoadoutV2InputLive", Hidden)!;
        Assert.True((bool)live.Invoke(game, [false])!);

        bool composing = gate == "composing";
        if (gate == "command") Set(game, "_cameraMode", CameraMode.Command);
        else if (!composing) Set(game, gate, true);
        Assert.False((bool)live.Invoke(game, [composing])!);

        // The router sees a click, the wheel and every key while gated.
        Set(game, "_hangarScriptMouse", over);
        var ship = session.World.Ships[10];
        var everything = new KeyboardState(Keys.Down, Keys.Right, Keys.Space, Keys.Enter, Keys.E, Keys.D2, Keys.D,
            Keys.Tab, Keys.L, Keys.B);
        Call(game, "UpdateLoadoutV2Input", everything, default(Game1.PadNav), composing, ship);
        var router = Get<LoadoutV2Input>(game, "_loadoutV2InputState");
        Assert.Equal(7, shop.EffectiveHullId);
        Assert.Equal((LoadoutV2Column.Hulls, 0), (router.Column, router.HullFocus));
        Assert.Null(router.Hover);
        Assert.False(Get<bool>(game, "_hangarOnly"));
        if (gate != "_launchSent") Assert.False(Get<bool>(game, "_launchSent"));
        Assert.False((bool)Call(game, "LoadoutV2CloseOnEscape")!);
        // Touch taps arrive through HandleDockedTouch, which chat entry already gates.
        if (!composing) Call(game, "LoadoutV2Tap", row.Bounds.CenterX, row.Bounds.CenterY, ship);
        Assert.Equal(7, shop.EffectiveHullId);

        // The gate lifts with everything still held: nothing fires late.
        if (gate == "command") Set(game, "_cameraMode", CameraMode.Cockpit);
        else if (!composing) Set(game, gate, false);
        Call(game, "UpdateLoadoutV2Input", everything, default(Game1.PadNav), false, ship);
        Assert.Equal(7, shop.EffectiveHullId);
        Assert.Equal((LoadoutV2Column.Hulls, 0), (router.Column, router.HullFocus));
        Assert.False(Get<bool>(game, "_hangarOnly"));
        // Released and pressed again, the same click selects the hull.
        Set(game, "_hangarScriptMouse", Pointer(row.Bounds.CenterX, row.Bounds.CenterY, false));
        Call(game, "UpdateLoadoutV2Input", new KeyboardState(), default(Game1.PadNav), false, ship);
        Set(game, "_hangarScriptMouse", Pointer(row.Bounds.CenterX, row.Bounds.CenterY, true));
        Call(game, "UpdateLoadoutV2Input", new KeyboardState(), default(Game1.PadNav), false, ship);
        Assert.Equal(8, shop.EffectiveHullId);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Pad_buttons_reach_the_router_through_the_docked_pad_snapshot(HudStyle style)
    {
        using var session = new ConnectionSession();
        var shop = TwoHulls();
        var game = Docked(session, style, flag: true, shop);
        var (_, draw) = Drawn(game, shop);
        draw();
        var ship = session.World.Ships[10];
        void Pad(Game1.PadNav nav)
        {
            Call(game, "UpdateLoadoutV2Input", new KeyboardState(), nav, false, ship);
            Call(game, "UpdateLoadoutV2Input", new KeyboardState(), default(Game1.PadNav), false, ship);
        }
        Pad(new(false, false, false, false, false, false, false, RightShoulder: true));
        Assert.Equal(8, shop.EffectiveHullId);
        Pad(new(false, false, true, false, false, false, false));              // D-pad left
        Assert.Equal(7, shop.EffectiveHullId);
        Pad(new(false, true, false, false, false, false, false));              // D-pad down
        Pad(new(false, false, false, false, Accept: true, false, false));      // A
        Assert.Equal(8, shop.EffectiveHullId);
        Pad(new(false, false, false, false, false, false, false, Extra2: true)); // X
        Assert.Equal(LoadoutV2Column.Slots, Get<LoadoutV2Input>(game, "_loadoutV2InputState").Column);
        Pad(new(false, false, false, false, false, Back: true, false));        // B
        Assert.True(Get<bool>(game, "_hangarOnly"));
        Set(game, "_hangarOnly", false);
        Pad(new(false, false, false, false, false, false, Extra: true));       // Y
        Assert.True(Get<bool>(game, "_launchSent"));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Escape_closes_the_picker_first_and_custom_loadout_digits_keep_working(HudStyle style)
    {
        Require.Artwork();
        var catalog = ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork))!;
        var core = catalog.Core!;
        var civ = core.Civilizations.Values.First(c => c.Name == "Gigacorp");
        var offered = catalog.FilteredFor(core.StationTypes[civ.InitialStationTypeId].TtbmLocal,
            TechTreeBitMask.FromBytes(civ.TtbmBaseTechs));
        var seed = new ShopModel(offered, offered.Hulls.First().HullId, [], 10000);
        seed.ApplyDefaultLoadout();
        var shop = new ShopModel(offered, seed.EffectiveHullId, seed.ChosenParts.ToArray(), 10000);
        using var session = new ConnectionSession();
        var game = Docked(session, style, flag: true, shop);
        var (model, draw) = Drawn(game, shop);
        draw();
        var router = Get<LoadoutV2Input>(game, "_loadoutV2InputState");
        var docked = shop.ChosenParts.ToArray();

        // Escape: first the picker, then (false) the screen as today.
        Frame(game, Keys.D2);
        Frame(game, Keys.Space);
        Assert.NotNull(model.OpenSlot);
        Assert.True((bool)Call(game, "LoadoutV2CloseOnEscape")!);
        Assert.Null(model.OpenSlot);
        Assert.False((bool)Call(game, "LoadoutV2CloseOnEscape")!);
        Assert.False(Get<bool>(game, "_hangarOnly"));

        // Ctrl+3 saves the pending loadout (native load.cpp:2228-2235) ...
        int filled = Enumerable.Range(0, model.SlotRows.Count).First(i => model.SlotRows[i].Filled);
        model.ClearSlot(model.SlotRows[filled].Slot);
        var edited = shop.ChosenParts.ToArray();
        Assert.NotEqual(docked, edited);
        Frame(game, Keys.LeftControl, Keys.D3);
        model.Revert();
        Assert.Equal(docked, shop.ChosenParts);
        // ... and plain 3 restores it (load.cpp:2236-2247).
        Frame(game, Keys.D3);
        Assert.Equal(edited, shop.ChosenParts);

        // Ctrl+1 saves slot 1 and does not move the focus; plain 1 / 2 focus a
        // column and leave the pending loadout alone (the rebindable V2 verbs).
        Frame(game, Keys.LeftControl, Keys.D1);
        Assert.Equal(LoadoutV2Column.Slots, router.Column);
        model.Revert();
        Frame(game, Keys.D1);
        Assert.Equal(LoadoutV2Column.Hulls, router.Column);
        Assert.Equal(docked, shop.ChosenParts);
        Frame(game, Keys.D2);
        Assert.Equal(LoadoutV2Column.Slots, router.Column);
        Assert.Equal(docked, shop.ChosenParts);
        // Rebound off the digit, plain 1 is native's restore again.
        var map = KeyBindingStore.Instance.Map;
        var original = map.BindingOf(LoadoutV2Input.FocusHullsAction);
        try
        {
            map.SetBinding(LoadoutV2Input.FocusHullsAction, new(Keys.F9, InputModifiers.None), force: true);
            Frame(game, Keys.D1);
            Assert.Equal(edited, shop.ChosenParts);
            Assert.Equal(LoadoutV2Column.Slots, router.Column);
        }
        finally { map.SetBinding(LoadoutV2Input.FocusHullsAction, original, force: true); }

        // With the flag off the digits are untouched native behaviour.
        Get<VisualOptions>(game, "_visuals").SetToggle(VisualFlags.LoadoutV2Key, false);
        Set(game, "_chatActive", true);
        model.Revert();
        Frame(game, Keys.D1);
        Assert.Equal(edited, shop.ChosenParts);
    }
}
