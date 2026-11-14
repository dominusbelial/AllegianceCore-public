using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

// P1015 — Loadout V2 input wiring. The pure LoadoutV2Input router turns mouse,
// key-table and pad state into LoadoutV2Model actions; this partial only reads
// the devices, applies the screen gates and forwards Buy / Launch / Back to the
// paths the old loadout already uses (no wire change).
public partial class Game1
{
    private LoadoutV2Input? _loadoutV2InputState;
    private LoadoutV2Input LoadoutV2InputRouter => _loadoutV2InputState ??= new LoadoutV2Input();

    /// <summary>The V2 screen is the one drawn this frame (the draw branch in
    /// Game1.Rendering.cs: flag on, loadout view, no investment/teleport pane).
    /// While true the old loadout's hover bands, touch rows and shop keys are off.</summary>
    private bool LoadoutV2Shown => _visuals is { } visuals
        && LoadoutV2Presentation.UseV2(visuals, DockedLoadoutView) && !_investActive && !_teleportActive;

    /// <summary>P1006 gates: nothing on the V2 screen reacts while the command
    /// screen (inside <see cref="DockedLoadoutView"/>), chat entry or a popup has
    /// focus, or once a launch is on its way — the union of the gates the old
    /// loadout's keys, hover and touch use.</summary>
    private bool LoadoutV2InputLive(bool composing) => LoadoutV2Shown && !composing && !_launchSent
        && !_chatActive && !_qcMenuOpen && !_helpVisible && !_mainMenuVisible && !_keyBindVisible
        && !_graphicsSettingsVisible && !_creditsVisible && !_playerPingsVisible && !_teamPaneVisible
        && !_hangarQuitConfirm && !_trainingQuitConfirm;

    private LoadoutV2Model? LoadoutV2ModelFor(ShopModel? shop) =>
        shop is not null && _loadoutV2Model is { } model && ReferenceEquals(model.Shop, shop) ? model : null;

    /// <summary>Every docked frame: feed the router. It only observes (held keys,
    /// button, wheel) while the gates are closed, so nothing held across a popup
    /// or the hangar becomes a fresh press.</summary>
    private void UpdateLoadoutV2Input(KeyboardState ks, PadNav nav, bool composing, ShipEntity mySe)
    {
        // Flag off: no router is ever created and no device is read here.
        if (_loadoutV2InputState is null && !LoadoutV2Shown) return;
        bool live = LoadoutV2InputLive(composing);
        bool control = ks.IsKeyDown(Keys.LeftControl) || ks.IsKeyDown(Keys.RightControl);
        var keys = LoadoutV2Input.Keys(action => ActionDown(ks, action), control,
            new LoadoutV2Pad(nav.Up, nav.Down, nav.Left, nav.Right, nav.Accept, nav.Back, nav.X, nav.Extra,
                nav.LeftShoulder, nav.RightShoulder));
        // The scripted hangar pointer (real-window proofs, headless tests) wins,
        // exactly as it does for the hangar screen's own buttons.
        var mouse = HangarScriptMouse ?? ReadProofMouse();
        var pointer = new LoadoutV2Pointer(mouse.X, mouse.Y,
            mouse.LeftButton == ButtonState.Pressed, mouse.ScrollWheelValue);
        var result = LoadoutV2InputRouter.Step(LoadoutV2ModelFor(_shopModel), _loadoutV2Layout, live, keys, pointer);
        // Native slot MouseEnter cues mouseoverSound (WinTrek/load.cpp:233-237).
        if (result.HoverCue) UiSounds?.PlayMouseOver();
        ApplyLoadoutV2Request(result.Request, mySe);
    }

    /// <summary>Buy, Launch and Back on the old screen's own paths: native
    /// OnButtonBuy / OnButtonLaunch / OnButtonBack
    /// (WinTrek/load.cpp:1511-1531,1498-1509,1533-1538).</summary>
    private void ApplyLoadoutV2Request(LoadoutV2Request request, ShipEntity mySe)
    {
        if ((request & LoadoutV2Request.Buy) != 0) BuyFromHangar(mySe);
        if ((request & LoadoutV2Request.Launch) != 0) LaunchFromHangar(mySe);
        // Same rule as Escape (training keeps the loadout, console.cpp:1128-1131).
        if ((request & LoadoutV2Request.Leave) != 0 && !TrainingActive)
        {
            _hangarOnly = true;
            Console.WriteLine("[monogame] loadout V2 -> hangar (back)");
        }
    }

    /// <summary>Escape closes the open picker first; false lets Escape leave the
    /// screen as it does today.</summary>
    private bool LoadoutV2CloseOnEscape() => LoadoutV2Shown
        && LoadoutV2ModelFor(_shopModel) is { } model && LoadoutV2InputRouter.ClosePicker(model);

    /// <summary>A touch tap on the V2 screen: the drawn rectangles are the only
    /// targets (DockedTouchZones.LoadoutV2HitTest).</summary>
    private void LoadoutV2Tap(float x, float y, ShipEntity mySe)
    {
        if (!LoadoutV2InputLive(composing: false) || _loadoutV2Layout is not { } layout
            || LoadoutV2ModelFor(_shopModel) is not { } model) return;
        ApplyLoadoutV2Request(LoadoutV2InputRouter.Tap(model, layout, x, y), mySe);
    }

    /// <summary>The docked key block while the V2 screen is shown: the router
    /// (above) already handled the shop verbs, so only the verbs with no V2
    /// control remain — undock, and the native custom-loadout digits. The old
    /// screen's latches follow the keys so nothing held fires when it returns.</summary>
    private void HandleLoadoutV2DockedKeys(ShopModel shop, KeyboardState ks, PadNav nav)
    {
        bool uKey = ActionDown(ks, "ShopUndock");
        if (uKey && !_dockUWasDown && !_launchSent)
        {
            _session.SendDocked();
            _launchSent = true;
            _throttle = Flight.FlightInput.DefaultThrottle;
            Console.WriteLine("[monogame] UNDOCK (no buy) -> FM_C_DOCKED(NA)");
        }
        _dockUWasDown = uKey;
        _dockUpWasDown = ActionDown(ks, "MenuUp") || nav.Up;
        _dockDownWasDown = ActionDown(ks, "MenuDown") || nav.Down;
        _dockEnterWasDown = ActionDown(ks, "MenuConfirm") || nav.Accept;
        _dockSpaceWasDown = ActionDown(ks, "MenuSelect") || _dockEnterWasDown;
        _dockLWasDown = ActionDown(ks, "ShopLaunch") || nav.Extra;
        _dockTabWasDown = ActionDown(ks, "ShopTabSwitch") || nav.LeftShoulder || nav.RightShoulder;
        _dockQWasDown = ActionDown(ks, "ShopCycleHullPrev") || nav.Left;
        _dockEWasDown = ActionDown(ks, "ShopCycleHullNext") || nav.Right;
        _dockBWasDown = ActionDown(ks, "MenuBack") || nav.Back;
        HandleCustomLoadoutKeys(shop, ks);
    }

    /// <summary>True for a plain digit bound to a V2 focus verb: on the V2 screen
    /// it focuses a column and does not also restore that custom loadout.</summary>
    private bool LoadoutV2FocusDigit(Keys key)
    {
        foreach (var action in new[] { LoadoutV2Input.FocusHullsAction, LoadoutV2Input.FocusSlotsAction })
        {
            var binding = KeyOf(action);
            if (binding.Key == key && binding.Modifiers == InputModifiers.None) return true;
        }
        return false;
    }
}
