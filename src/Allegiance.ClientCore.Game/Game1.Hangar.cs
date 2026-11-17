using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly HangarLaunchSequence _hangarLaunch = new();
    private long _hangarBoardFailure;
    private bool _hangarBuyAfterDisembark;
    private HangarAction _hangarHover;
    private bool _hangarMouseDown;
    private bool _hangarQuitConfirm;
    private readonly ActionPressEdges _hangarEdges = new();
    private bool HangarDevelopments => _session.World.ResearchBriefing?.Settings.AllowDevelopments ?? true;

    // P945 — the native hangar screen (hangar.mdl). The renderer owns the
    // artwork; the launch pane's FrameImage animation lives in UpdateHangarControls
    // so draw and hit-test share one clock. When the artwork is absent the
    // pre-P945 text-row hangar stays as the fallback (never a blank screen).
    private Rendering.HangarScreenRenderer? _hangarScreenRenderer;
    private readonly Rendering.HangarLaunchAnimation _hangarLaunchAnimation = new();

    /// <summary>P945 — whether the native hangar screen owns the window this
    /// frame: docked AND in the hangar view (native vmHangar). Native hides the
    /// flight HUD and sets the radar LOD to none there
    /// (<c>WinTrek/WinTrek.cpp:6714</c>, <c>:6836</c>).</summary>
    private bool _nativeHangarScreen => _state == ClientState.Flying
        && _dockedActive && _hangarOnly && !CommandCameraActive;

    private bool DockedLoadoutView => _state == ClientState.Flying
        && _dockedActive && !_hangarOnly && !CommandCameraActive && _shopModel is not null;

    // WinTrek.cpp:6706-6740 replaces the screen (including its PaneImage) on
    // vmCommand/vmLoadout. A docked ship alone does not make hangar controls live.
    private bool HangarControlsActive => _nativeHangarScreen
        && !_investActive && !_teleportActive && !_teamPaneVisible
        && !_mainMenuVisible && !_helpVisible && !_keyBindVisible
        && !_graphicsSettingsVisible && !_creditsVisible && !_playerPingsVisible;

    /// <summary>P1042 — the shared native hangar artwork, preloaded at LoadContent so a
    /// skin switch never decodes art during Draw (P972 §2.5). Both screen adapters draw
    /// the same content; only the chrome above it differs.</summary>
    private Rendering.HangarScreenRenderer BuildHangarScreenRenderer() =>
        new(_spriteBatch, _whitePixel, _hudSmall, _nativeFonts, GraphicsDevice, _assets);

    private void LaunchFromHangar(ShipEntity ship)
    {
        if (_launchSent || _hangarLaunch.AwaitingDisembark) return;
        if (ship.ParentId >= 0)
        {
            _hangarBoardFailure = _session.World.BoardFailureRevision;
            _hangarLaunch.Begin(ship.ParentId);
            _session.SendBoardShip(-1);
            return;
        }
        if (_shopModel is not { } shop || BuyFlow.Launch(shop) is not { Step: LoadoutBuyFlow.Step.Purchase } order) return;
        PurchaseHangarLoadout(order.HullId, order.Parts, launch: true);
        _launchSent = true;
        _throttle = Flight.FlightInput.DefaultThrottle;
    }

    private void ContinueHangarLaunch(ShipEntity ship)
    {
        // BOARD_NACK ends native teleport lockdown (clintlib/appmsg.cpp:1899-1911).
        if (_hangarLaunch.AwaitingDisembark && _session.World.BoardFailureRevision != _hangarBoardFailure)
            _hangarLaunch.Cancel();
        else if (_hangarLaunch.Acknowledged(ship.ParentId, ship.StationId >= 0))
        {
            if (_hangarBuyAfterDisembark)
            {
                _hangarBuyAfterDisembark = false;
                BuyFromHangar(ship);
            }
            else
            {
                LaunchFromHangar(ship);
            }
        }
    }

    /// <summary>
    /// P759 — the native <c>OnButtonBuy</c> action (<c>load.cpp:1503-1521</c>):
    /// when the loadout is not affordable the client sends <c>REQUEST_MONEY</c>
    /// for the shortfall (<c>cost = new - (current + money)</c>); otherwise it
    /// buys without launching (<c>BuyLoadout(pship, false)</c>) and saves the
    /// custom loadout slot 0. A boarded ship first disembarks (native
    /// <c>DisembarkAndBuy</c>), reusing the existing launch disembark sequence
    /// with a buy-after-disembark flag.
    /// </summary>
    private void BuyFromHangar(ShipEntity ship)
    {
        if (_launchSent || _hangarLaunch.AwaitingDisembark) return;
        if (ship.ParentId >= 0)
        {
            _hangarBoardFailure = _session.World.BoardFailureRevision;
            _hangarBuyAfterDisembark = true;
            _hangarLaunch.Begin(ship.ParentId);
            _session.SendBoardShip(-1);
            return;
        }
        if (_shopModel is not { } shop) return;
        // P1017 — one flow for both screens. Only V2 has native's disabled Buy.
        var order = BuyFlow.Buy(shop, nativeGate: LoadoutV2Shown);
        switch (order.Step)
        {
            case LoadoutBuyFlow.Step.RequestMoney:
                _session.SendMoneyRequest(order.Amount, order.HullId);
                Console.WriteLine($"[monogame] BUY short ${order.Amount} -> REQUEST_MONEY hull {order.HullId}");
                break;
            case LoadoutBuyFlow.Step.Purchase:
                PurchaseHangarLoadout(order.HullId, order.Parts, launch: false);
                Console.WriteLine($"[monogame] purchase hull {order.HullId} + {order.Parts.Count} part(s)");
                break;
        }
    }

    /// <summary>P1017 — the shared Buy / Launch flow and its reconciliation with
    /// the server's answer (<see cref="LoadoutBuyFlow"/>).</summary>
    // Lazy: headless tests build Game1 without running field initialisers.
    private LoadoutBuyFlow? _loadoutBuyFlowState;
    private LoadoutBuyFlow BuyFlow => _loadoutBuyFlowState ??= new LoadoutBuyFlow();

    /// <summary>Every docked frame: when the server acknowledged a loadout, the
    /// shop takes the ship it really has now. A launch the server did not
    /// perform releases the launch latch, as native ends its lock-down
    /// (<c>clintlib/appmsg.cpp:3180-3181</c>).</summary>
    private void SyncShopWithServer(ClientWorld world, ShipEntity own)
    {
        if (_shopModel is not { } shop) return;
        long? richest = null;
        foreach (var (shipId, money) in world.WalletByShipId)
            if (shipId != own.ShipId && (richest is not { } best || money > best)) richest = money;
        var outcome = BuyFlow.Sync(shop, own, world.OwnHardware, world.MyMoney,
            trackFreeHardware: _embeddedHost is null, richestTeammate: richest);
        if (!outcome.Changed) return;
        if (outcome.LaunchRefused && _launchSent && own.StationId >= 0)
        {
            _launchSent = false;
            Console.WriteLine("[monogame] launch not performed by the server -> loadout screen released");
        }
        if (outcome.Answered && !outcome.Complete)
            Console.WriteLine($"[monogame] purchase not completed: {shop.BlockReason}");
    }

    private void PurchaseHangarLoadout(short hullId, IReadOnlyList<ExpandedPartData> parts, bool launch)
    {
        if (_embeddedHost is not { } training)
        {
            _session.SendBuyLoadout(hullId, fLaunch: launch, parts: parts);
            return;
        }
        // clintlib.cpp:1731-1744: the loadout and completion event precede
        // station launch in the same call. The receive thread cannot interleave
        // loopback updates with this transaction.
        lock (_session.Gate)
            training.BuyLoadout(hullId, parts, launch,
                ship => _session.CompleteLocalTrainingPurchase(ship.HullType.HullId, ship.ExportInventoryForAck()),
                ship => _session.ApplyLocalTrainingLaunch(ship.Sector!.Id,
                    ServerShipUpdate.FromModel(ship.Id, ship, _session.World.Ships[ship.Id].LastUpdateTick)));
    }

    // This dispatcher is shared by the real pointer buttons and headless transition tests.
    private void ActivateHangar(HangarAction action)
    {
        if (_launchSent || _hangarLaunch.AwaitingDisembark || _hangarQuitConfirm) return;
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var ship) || ship.StationId < 0) return;
        switch (action)
        {
            case HangarAction.Launch: LaunchFromHangar(ship); break;
            case HangarAction.Buy: BuyFromHangar(ship); break;
            case HangarAction.Default:
                _shopModel?.ApplyDefaultLoadout();
                break;
            case HangarAction.Command:
                if (!world.Stations.TryGetValue(ship.StationId, out var station)) break;
                ApplyConsoleMode("ViewCommand", updatePointer: false);
                _session.SendViewClusterRequest(station.ClusterId);
                _cameraMode = CameraMode.Command;
                _commandCentered = false;
                _directorActive = _freeCamActive = _orbitCamActive = false;
                break;
            case HangarAction.Loadout:
                _hangarOnly = false; _investActive = _teleportActive = _teamPaneVisible = false;
                _helpVisible = false; _cameraMode = CameraMode.Cockpit;
                break;
            case HangarAction.Invest:
                if (!HangarDevelopments) break;
                _investActive = !_investActive; _teleportActive = _teamPaneVisible = false;
                break;
            case HangarAction.Teleport:
                _teleportActive = !_teleportActive; _investActive = _teamPaneVisible = false;
                if (_teleportActive) _teleportModel = BuildTeleportModel(world, ship);
                break;
            case HangarAction.Team:
                ApplyConsoleMode("TeamPane", updatePointer: false); _hangarOnly = true; break;
            case HangarAction.Help: _helpVisible = true; break;
            case HangarAction.Exit: if (!RequestTrainingQuit()) _hangarQuitConfirm = true; break;
        }
    }

    private void ConfirmHangarQuit(bool confirm)
    {
        if (!_hangarQuitConfirm) return;
        _hangarQuitConfirm = false;
        if (!confirm) return;
        _hangarLaunch.Cancel();
        if (_skirmish?.Options.HostLan == true) { StopSkirmish(); return; }
        if (_session.World.MyShipId is { } me) _session.SendQuitMission(me, QuitSideReason.Quit);
        // The normal authoritative OwnMissionDeparture path returns to the browser.
    }

    private bool HandleHangarQuitInput(KeyboardState keys, bool composing)
    {
        bool confirm = _hangarEdges.Observe("confirm", ActionDown(keys, "MenuConfirm"));
        bool cancel = _hangarEdges.Observe("cancel", ActionDown(keys, "MenuBack") || ActionDown(keys, "MainMenu"));
        if (!_hangarQuitConfirm) return false;
        if (!composing && (confirm || cancel)) ConfirmHangarQuit(confirm && !cancel);
        return true;
    }

    private bool UpdateHangarControls(KeyboardState keys, bool composing)
    {
        // P945 — --auto-hangar scripts the pointer over the native panes for the
        // real-window proof; inert unless the flag was passed.
        StepHangarScript();
        var mouse = HangarScriptMouse ?? Mouse.GetState();
        bool down = mouse.LeftButton == ButtonState.Pressed;
        bool click = down && !_hangarMouseDown;
        _hangarMouseDown = down;
        bool allowed = !composing && HangarControlsActive;
        // Observe the button even while hidden: a held click must not become
        // a fresh press when the player returns to the hangar.
        if (!allowed)
        {
            _hangarHover = HangarAction.None;
            _hangarLaunchAnimation.Update(false, (float)(Environment.TickCount64 / 1000.0));
            return _hangarQuitConfirm;
        }
        var vp = GraphicsDevice.Viewport;
        _hangarHover = HangarControls.Hit(vp.Width, vp.Height, mouse.X, mouse.Y, HangarDevelopments);
        // P945 — the launch pane's FrameImage ramp (0.7 s in/out, no loop, no
        // finish; engine/frameimage.cpp:420-456). It runs on the wall clock so a
        // frame drop cannot stall the door half-open.
        _hangarLaunchAnimation.Update(allowed && _hangarHover == HangarAction.Launch,
            (float)(Environment.TickCount64 / 1000.0));
        if (_hangarQuitConfirm) return true;
        if (allowed && click && _hangarHover != HangarAction.None)
        {
            ActivateHangar(_hangarHover);
            return true;
        }
        return _hangarQuitConfirm || _state != ClientState.Flying;
    }

    private void DrawHangarQuitConfirmation()
    {
        if (!_trainingQuitConfirm && (!_hangarQuitConfirm || !_dockedActive || _state != ClientState.Flying)) return;
        var vp = GraphicsDevice.Viewport;
        var panel = new Rectangle(vp.Width / 2 - 280, vp.Height / 2 - 70, 560, 140);
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        _spriteBatch.Draw(_whitePixel, panel, new Color(12, 30, 40));
        string question = _trainingQuitConfirm ? "Quit the Current Training Mission?" : "QUIT CURRENT MISSION?";
        float scale = MathF.Min(1f, (panel.Width - 40f) / _hudBig.MeasureString(question).X);
        _spriteBatch.DrawString(_hudBig, question, new Vector2(panel.X + 20, panel.Y + 20), Color.LightCyan,
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        _spriteBatch.DrawString(_hudSmall, $"{KeyOf("MenuConfirm").Label} confirm    {KeyOf("MainMenu").Label} cancel",
            new Vector2(panel.X + 20, panel.Y + 88), Color.White);
        _spriteBatch.End();
    }

    private void DrawHangarControls()
    {
        // The native hangar screen IS the docked hangar view (vmHangar,
        // WinTrek.cpp:6706-6718); the loadout view (vmLoadout) has its own screen.
        if (!_nativeHangarScreen) return;
        var vp = GraphicsDevice.Viewport;
        // P1042 — the screen-chrome boundary: Classic/Modern draw the untouched native
        // screen; Jet draws the same artwork unchanged and layers vector chrome over
        // the same HangarScreen rectangles the pointer hit test uses. Missing artwork
        // keeps the pre-P945 text-row fallback, never a blank screen.
        if (ActiveScreen.HangarArtAvailable)
        {
            ActiveScreen.DrawHangar(new HangarScreenFrame(vp, _hangarHover, _hangarLaunchAnimation,
                LoadoutBoardState()?.State, HangarDevelopments, HangarScreenInks));
            return;
        }
        DrawHangarControlsTextFallback(vp);
    }

    /// <summary>P1042 — the hangar frame's semantic inks. Classic/Modern's hangar
    /// drawing keeps the native hover colour and artwork untouched (these values are
    /// the native <c>hugeFont</c> grey and the port's warning ink); Jet maps its palette
    /// instead, so the values are documentation for the classic adapter.</summary>
    private static readonly ScreenInks HangarScreenInks =
        new(HangarScreen.HoverTextColor, Color.White, HangarScreen.HoverTextColor, Color.Orange);

    /// <summary>The pre-P945 presentation, kept as the missing-artwork fallback:
    /// one row of text cells at the bottom of the viewport plus the hover/quit
    /// feedback line. Uses the same <see cref="HangarScreen"/> rectangles as the
    /// native screen, so a click lands on the verb it is drawn over.</summary>
    private void DrawHangarControlsTextFallback(Viewport vp)
    {
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        foreach (var action in HangarControls.Actions(HangarDevelopments))
        {
            var rect = HangarControls.Bounds(vp.Width, vp.Height, action);
            _spriteBatch.Draw(_whitePixel, rect, action == _hangarHover ? new Color(30, 100, 105) : new Color(12, 30, 40));
            string text = action == HangarAction.Exit ? "Quit mission" : action.ToString();
            float scale = MathF.Min(1f, (rect.Width - 8) / _hudSmall.MeasureString(text).X);
            _spriteBatch.DrawString(_hudSmall, text, new Vector2(rect.X + 4, rect.Y + 5), Color.White,
                0, Vector2.Zero, scale, SpriteEffects.None, 0);
        }
        string feedback = _hangarQuitConfirm ? $"Leave this mission? {KeyOf("MenuConfirm").Label} confirm / {KeyOf("MainMenu").Label} cancel" : HangarControls.Feedback(_hangarHover);
        _spriteBatch.Draw(_whitePixel, new Rectangle(16, vp.Height - 38, vp.Width - 32, 26), new Color(8, 18, 26));
        _spriteBatch.DrawString(_hudSmall, feedback, new Vector2(22, vp.Height - 34), Color.LightCyan);
        _spriteBatch.End();
    }
}
