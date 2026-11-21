using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P1070 — the browser's docked hangar and loadout screens, drawn in the game window.
/// This is the browser copy of the desktop glue: the drawing and input plumbing that
/// mirrors <c>Game1.Hangar.cs:216-331</c> (the hangar screen, its hover strip and its
/// launch-pane animation), <c>Game1.LoadoutV2.cs:26-143</c> (the V2 screen, the hull
/// preview and the hardpoint marker) and <c>Game1.LoadoutV2Input.cs</c> (the router,
/// the gates, the Buy/Launch/Back dispatch). The state machine and every rule live in
/// <see cref="BrowserHangarModel"/>; this partial only reads devices and draws.
///
/// <para><b>Rendering path.</b> Classic and Modern both go through
/// <see cref="HangarScreenRenderer"/> and <see cref="LoadoutV2Renderer"/> — the exact
/// pair <c>ClassicScreenSkinRenderer.DrawHangar/DrawLoadout</c>
/// (<c>Rendering/Skins/ClassicScreenSkinRenderer.cs:80-94</c>) forward to, so both HUD
/// styles get the native hangar art and the V2 composition with their own typography
/// and scaling, at 1920×1080 and 800×480 alike. The native screen is laid out in
/// 800×600 and scaled uniformly and centred (<see cref="HangarScreen.ScreenTransform"/>),
/// so the letterbox margins are the only difference between the two sizes.</para>
///
/// <para><b>Missing artwork.</b> The pre-P945 text-row hangar stays as the fallback
/// (<c>Game1.Hangar.cs:288-320</c>), drawing the same <see cref="HangarControls"/>
/// rectangles the pointer hit test uses, so a hangar without its art is still usable —
/// never a blank window.</para>
/// </summary>
public sealed partial class Game1
{
    private BrowserHangarModel? _hangarField;
    private HangarScreenRenderer? _hangarScreenRenderer;
    private readonly HangarLaunchAnimation _hangarLaunchAnimation = new();
    private NativeFonts? _hangarFonts;
    private LoadoutV2Renderer? _loadoutV2Renderer;
    private LoadoutV2Input? _loadoutV2InputState;
    private LoadoutV2Layout? _loadoutV2Layout;
    private float _hangarPreviewSpin;
    private float _hangarSeconds;

    // The pointer the DOM forwards while the docked screen is up. There is no
    // KNI mouse position in the browser (the canvas is the only surface), so the
    // page feeds the same three values the desktop reads from Mouse.GetState().
    private int _hangarPointerX = -1, _hangarPointerY = -1;
    private bool _hangarPointerDown, _hangarPointerWasDown;
    private int _hangarWheel;
    private KeyboardState _hangarKeys, _hangarLastKeys;

    /// <summary>The browser hangar state machine (lazily created so a headless Game1
    /// image never depends on a field initializer).</summary>
    public BrowserHangarModel Hangar => _hangarField ??= new BrowserHangarModel
    {
        Catalog = _shopCatalog ?? ShopCatalog.Empty,
        KeyLabel = action => KeyBindingStore.Instance.Map.BindingOf(action).Label,
        // The desktop's own wire calls (Game1.Hangar.cs:155,113,59,215;
        // Game1.LoadoutV2Input.cs:129): same shared ConnectionSession methods.
        SendBuyLoadout = (hullId, launch, parts) => _session.SendBuyLoadout(hullId, launch, parts),
        SendMoneyRequest = (amount, hullId) => _session.SendMoneyRequest(amount, hullId),
        SendBoardShip = parent => _session.SendBoardShip(parent),
        SendDocked = () => _session.SendDocked(),
        SendQuitMission = (shipId, reason) => _session.SendQuitMission(shipId, reason),
    };

    /// <summary>True while the game window owns the docked screen (the page's "hangar
    /// visible" hook).</summary>
    public bool HangarVisible => _hangarField?.Active == true;

    /// <summary>True while the loadout screen is up inside the game window.</summary>
    public bool LoadoutOpen => _hangarField?.Active == true
        && _hangarField.Screen == BrowserHangarScreen.Loadout;

    /// <summary>The state the JS <c>gameMenu.hangar()</c> hook reports: what the player
    /// is looking at, what it costs, and why an action is refused — the same numbers
    /// the drawn screen shows.</summary>
    public object HangarMetrics
    {
        get
        {
            var hangar = _hangarField;
            var shop = hangar?.Shop;
            return new
            {
                visible = hangar?.Active == true,
                screen = hangar?.Screen.ToString() ?? BrowserHangarScreen.Hangar.ToString(),
                loadoutOpen = LoadoutOpen,
                boardState = hangar?.BoardState,
                developments = hangar?.Developments ?? true,
                hover = hangar?.Hover.ToString() ?? "None",
                status = hangar?.Status ?? "",
                lastAction = hangar?.LastAction ?? "",
                launchSent = hangar?.LaunchSent == true,
                launchPending = hangar?.LaunchPending == true,
                quitConfirm = hangar?.QuitConfirm == true,
                blocker = hangar?.LastReadiness.Blocker.ToString() ?? "NoSession",
                blockerText = HangarLaunchText.For(hangar?.LastReadiness.Blocker ?? HangarLaunch.Blocker.NoSession),
                money = shop?.MyMoney,
                hull = shop?.EffectiveHullId,
                hullName = shop?.EffectiveHull?.Name,
                currentHull = shop?.CurrentHullId,
                parts = shop?.ChosenParts.Count ?? 0,
                offeredHulls = shop?.BuyableHulls.Count ?? 0,
                offeredParts = shop?.Catalog.Parts.Count ?? 0,
                cost = shop?.Cost ?? 0,
                shortfall = shop?.BuyShortfall ?? 0,
                buyEnabled = shop?.BuyEnabled == true,
                launchEnabled = shop?.LaunchEnabled == true,
                state = shop?.State.ToString() ?? "Unknown",
                blockReason = shop?.BlockReason,
                slots = shop?.Slots.Count ?? 0,
                offers = shop is null || shop.OpenSlot is not { } open ? 0 : shop.SlotOffers(open).Count,
                // Live mesh readiness of the previewed hull: the P1065 streamer
                // request is made every docked frame and the preview shows a
                // stand-in until the verified pack lands.
                previewStreamed = _battleScene?.MeshBytesReady(shop?.EffectiveHullId ?? -1,
                    (Allegiance.ClientCore.Browser.Assets.StagedAssetSource)_assets) == true,
                // The hangar verbs' live rectangles — the same HangarControls.Bounds the
                // pointer hit test and the renderer use, so a harness clicks exactly
                // where the button is drawn.
                verbs = hangar?.Active != true ? [] : HangarControls.Actions(hangar.Developments)
                    .Select(action => new
                    {
                        action = action.ToString(),
                        bounds = HangarControls.Bounds(GraphicsDevice.Viewport.Width,
                            GraphicsDevice.Viewport.Height, action),
                    }).ToArray(),
                // The loadout screen's drawn hit targets, at the layout this frame drew
                // (draw == hit): the harness clicks a hull row, a slot, an offer and the
                // Buy/Launch buttons exactly where they are painted.
                loadout = _loadoutV2Layout is null ? null : new
                {
                    openSlot = _loadoutV2Layout.ExpandedSlot,
                    hullFocus = hangar?.Frame?.HullFocus ?? -1,
                    elements = _loadoutV2Layout.Elements.Select(element => new
                    {
                        kind = element.Kind.ToString(),
                        index = element.Index,
                        bounds = new
                        {
                            x = element.Bounds.X, y = element.Bounds.Y,
                            width = element.Bounds.Width, height = element.Bounds.Height,
                        },
                    }).ToArray(),
                },
            };
        }
    }

    /// <summary>The hangar's Launch verb for a pointer or a harness: exactly what a click
    /// on the native launch pane does (<c>Game1.Hangar.cs:170-172</c> →
    /// <see cref="BrowserHangarModel.Launch"/>).</summary>
    public bool HangarLaunchVerb()
    {
        if (_flightSession is null) return false;
        var world = _flightSession.World;
        if (world.MyShipId is not { } id || !world.Ships.TryGetValue(id, out var own)) return false;
        Hangar.Launch(world, own, HangarLaunch.Of(_flightSession),
            world.Mission?.Stage == MissionStageMessage.StageStarted);
        return true;
    }

    /// <summary>The DOM pointer for the docked screens (canvas coordinates, the same
    /// space <see cref="HangarScreen.Rect"/> and <see cref="LoadoutV2Layout"/> use).</summary>
    public void HangarPointer(int x, int y, bool down, int wheel)
    {
        _hangarPointerX = x; _hangarPointerY = y;
        _hangarPointerDown = down;
        _hangarWheel = wheel;
    }

    /// <summary>The docked keys (the same <c>FlightKeys</c> stream the flight pass
    /// reads; <c>wwwroot/flight-input.js</c> forwards it while the hangar is up too).</summary>
    private void HangarKeys() => _hangarKeys = _flightKeys;

    /// <summary>The page's launch pipeline asks the model to release its latch when it
    /// finishes (success or a refused click).</summary>
    public void HangarLaunchFinished(bool launched, string? reason) =>
        _hangarField?.LaunchAttemptFinished(launched, reason);

    /// <summary>One shot: did the game window ask the page to run its launch pipeline?</summary>
    public bool TakeHangarLaunchRequest() => _hangarField?.TakeLaunchRequest() == true;

    /// <summary>Session reset: the hangar forgets the dock, the shop and every latch
    /// (<c>Game1.Flight.cs:88-101</c> <c>AttachOnlineSession</c>).</summary>
    private void ResetBrowserHangar()
    {
        _hangarField?.Reset();
        _hangarPreviewSpin = 0;
    }

    private void EnsureHangarRenderers()
    {
        if (_hangarScreenRenderer is null)
        {
            // The two native font bakes the desktop's hangar renderer takes
            // (Game1.Hangar.cs:53-55 BuildHangarScreenRenderer): the same atlas the
            // flight HUD already staged, so no extra join bytes.
            _hangarFonts ??= new NativeFonts(Enum.GetValues<NativeFont>().ToDictionary(font => font,
                font => (Content.Load<SpriteFont>(NativeFontCatalog.Spec(font).Asset1x),
                         Content.Load<SpriteFont>(NativeFontCatalog.Spec(font).Asset2x))));
            _hangarScreenRenderer = new HangarScreenRenderer(_spriteBatch, _whitePixel, _hudSmall,
                _hangarFonts, GraphicsDevice, _assets);
        }
        _loadoutV2Renderer ??= new LoadoutV2Renderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
    }

    /// <summary>The docked frame: advance <see cref="BrowserHangarModel"/> and draw the
    /// screen the player is looking at. Called from the single docked branch of
    /// <c>DrawOnlineFlight</c>; the browser's draw pass follows its update pass in the
    /// same tick, so nothing is a frame stale.</summary>
    private void DrawBrowserHangar()
    {
        var world = _session.World;
        if (world.MyShipId is not { } id || !world.Ships.TryGetValue(id, out var own)) return;
        // P799 ghost gate (Game1.Panes.cs:106-126): a ghost lands in the command view,
        // never the hangar; the browser has no command view, so it says why.
        if (world.OwnIsGhost)
        {
            _flightDocked!.Draw(GraphicsDevice.Viewport,
                [new(ShopLayout.Kind.Title, "COMMAND VIEW"), new(ShopLayout.Kind.Info, own.CharacterName),
                 new(ShopLayout.Kind.Info, "Ghosts cannot buy or launch again in this mission.")], money: world.MyMoney);
            return;
        }
        if (own.StationId < 0) return;
        _hangarSeconds += (float)_flightElapsedSeconds;
        var readiness = HangarLaunch.Of(_session);
        bool missionStarted = world.Mission?.Stage == MissionStageMessage.StageStarted;
        var hangar = Hangar;
        hangar.Catalog = _shopCatalog ?? ShopCatalog.Empty;
        HangarKeys();
        if (!hangar.Update(world, own, readiness, missionStarted))
        {
            _flightDocked!.Draw(GraphicsDevice.Viewport,
                [new(ShopLayout.Kind.Title, "HANGAR"), new(ShopLayout.Kind.Info, own.CharacterName),
                 new(ShopLayout.Kind.Info, "Waiting for the docked ship…")], money: world.MyMoney);
            return;
        }
        EnsureHangarRenderers();
        var vp = GraphicsDevice.Viewport;
        if (hangar.Screen == BrowserHangarScreen.Hangar)
        {
            // The router is suspended while its screen is not shown, exactly as on the
            // desktop (Game1.LoadoutV2Input.cs:44-50): a held key or button never becomes
            // a fresh press when the loadout returns.
            _loadoutV2InputState?.Suspend(hangar.ViewModel);
            UpdateHangarScreen(hangar, vp);
        }
        else UpdateLoadoutScreen(hangar, world, own, readiness, missionStarted, vp);
        _hangarLastKeys = _hangarKeys;
        _hangarPointerWasDown = _hangarPointerDown;
    }

    /// <summary>The hangar screen's own frame (<c>Game1.Hangar.cs:222-268</c>
    /// <c>UpdateHangarControls</c>): hover from the shared rectangles, the launch pane's
    /// FrameImage ramp (<c>engine/frameimage.cpp:420-456</c>), a click edge dispatch and
    /// the quit confirmation keys.</summary>
    private void UpdateHangarScreen(BrowserHangarModel hangar, Viewport vp)
    {
        bool click = _hangarPointerDown && !_hangarPointerWasDown;
        var hover = _hangarPointerX >= 0
            ? HangarControls.Hit(vp.Width, vp.Height, _hangarPointerX, _hangarPointerY, hangar.Developments)
            : HangarAction.None;
        hangar.SetHover(hover);
        bool over = hover != HangarAction.None && !hangar.QuitConfirm;
        _hangarLaunchAnimation.Update(over && hover == HangarAction.Launch, _hangarSeconds);

        if (hangar.QuitConfirm)
        {
            bool confirm = Edge(_hangarKeys, _hangarLastKeys, "MenuConfirm");
            bool cancel = Edge(_hangarKeys, _hangarLastKeys, "MenuBack") || Edge(_hangarKeys, _hangarLastKeys, "MainMenu");
            if (confirm || cancel) hangar.ConfirmQuit(confirm && !cancel, _session.World.MyShipId ?? -1);
        }
        else if (click && hover != HangarAction.None)
        {
            var world = _session.World;
            if (world.MyShipId is { } id && world.Ships.TryGetValue(id, out var own))
                hangar.Activate(hover, world, own, HangarLaunch.Of(_session),
                    world.Mission?.Stage == MissionStageMessage.StageStarted);
        }
        DrawHangarScreen(hangar, vp);
    }

    private void DrawHangarScreen(BrowserHangarModel hangar, Viewport vp)
    {
        // The native screen is opaque and fills the window; black the letterbox
        // margins so the last 3-D frame never shows through (Game1.Hangar.cs:222-231).
        _spriteBatch.Begin();
        _spriteBatch.Draw(_whitePixel, vp.Bounds, Color.Black);
        _spriteBatch.End();
        if (_hangarScreenRenderer!.CanDrawNative)
        {
            _hangarScreenRenderer.Draw(vp, hangar.Hover, _hangarLaunchAnimation,
                hangar.BoardState, hangar.Developments);
        }
        else DrawHangarTextFallback(hangar, vp);
        DrawHangarStatus(hangar, vp);
    }

    /// <summary>The pre-P945 text-row hangar, kept as the missing-artwork fallback
    /// (<c>Game1.Hangar.cs:288-320</c>): one cell per verb at the shared rectangle.</summary>
    private void DrawHangarTextFallback(BrowserHangarModel hangar, Viewport vp)
    {
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        foreach (var action in HangarControls.Actions(hangar.Developments))
        {
            var rect = HangarControls.Bounds(vp.Width, vp.Height, action);
            _spriteBatch.Draw(_whitePixel, rect,
                action == hangar.Hover ? new Color(30, 100, 105) : new Color(12, 30, 40));
            string text = action == HangarAction.Exit ? "Quit mission" : action.ToString();
            float scale = MathF.Min(1f, (rect.Width - 8) / _hudSmall.MeasureString(text).X);
            _spriteBatch.DrawString(_hudSmall, text, new Vector2(rect.X + 4, rect.Y + 5), Color.White,
                0, Vector2.Zero, scale, SpriteEffects.None, 0);
        }
        _spriteBatch.End();
    }

    /// <summary>The hover strip's replacement text plus the launch/buy status line. The
    /// native hover string is drawn by the renderer itself
    /// (<c>HangarScreen.HoverText</c>); this band carries what the port has to add — the
    /// shared shop's refusal, the launch blocker and the busy line — at the same place
    /// the desktop's fallback prints its feedback (<c>Game1.Hangar.cs:315-319</c>).</summary>
    private void DrawHangarStatus(BrowserHangarModel hangar, Viewport vp)
    {
        string? line = hangar.QuitConfirm
            ? $"Leave this mission?  {KeyLabel("MenuConfirm")} confirm / {KeyLabel("MainMenu")} cancel"
            : hangar.Status.Length > 0 ? hangar.Status
            : hangar.LaunchPending ? HangarLaunchText.WaitingForServer
            : null;
        if (line is null) return;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        var band = new Rectangle(16, vp.Height - 38, vp.Width - 32, 26);
        _spriteBatch.Draw(_whitePixel, band, new Color(8, 18, 26));
        float scale = MathF.Min(1f, (band.Width - 12) / MathF.Max(1f, _hudSmall.MeasureString(line).X));
        _spriteBatch.DrawString(_hudSmall, line, new Vector2(band.X + 6, band.Y + 4), Color.LightCyan,
            0, Vector2.Zero, scale, SpriteEffects.None, 0);
        _spriteBatch.End();
    }

    private string KeyLabel(string action)
    {
        var label = KeyBindingStore.Instance.Map.BindingOf(action).Label;
        return string.IsNullOrEmpty(label) ? action : label;
    }

    /// <summary>The loadout frame (<c>Game1.LoadoutV2.cs:96-143</c>): the router gets the
    /// same keys and pointer the desktop feeds it, the presentation and layout are the
    /// shared ones, the hull preview is the real model under the loadout's own camera
    /// (<see cref="DockedHullOrbit"/>, <see cref="DockedHullPreviewFraming"/>), and Buy /
    /// Launch / Back dispatch through <see cref="BrowserHangarModel.Apply"/>.</summary>
    private void UpdateLoadoutScreen(BrowserHangarModel hangar, ClientWorld world, ShipEntity own,
        HangarLaunch.Readiness readiness, bool missionStarted, Viewport vp)
    {
        var router = _loadoutV2InputState ??= new LoadoutV2Input();
        hangar.Prepare(world, own);
        bool live = !hangar.LaunchSent && !hangar.LaunchPending && !hangar.QuitConfirm;
        bool control = _hangarKeys.IsKeyDown(Keys.LeftControl) || _hangarKeys.IsKeyDown(Keys.RightControl);
        var keys = LoadoutV2Input.Keys(action => ActionDown(_hangarKeys, action), control);
        var pointer = _hangarPointerX >= 0
            ? new LoadoutV2Pointer(_hangarPointerX, _hangarPointerY, _hangarPointerDown, _hangarWheel)
            : (LoadoutV2Pointer?)null;
        var result = router.Step(hangar.ViewModel, _loadoutV2Layout, live, keys, pointer);
        // Native Escape closes the open picker first, then leaves the loadout for the
        // hangar (Game1.LoadoutV2Input.cs:80-84 + the main-menu chain).
        if (Edge(_hangarKeys, _hangarLastKeys, "MainMenu")
            && !(hangar.ViewModel is { } escapeModel && router.ClosePicker(escapeModel)))
            hangar.Back();

        var frame = hangar.BuildFrame(world, own, router);
        if (frame is null) { DrawHangarScreen(hangar, vp); return; }
        // P1015: the layout scale is the shared HUD scale, so Classic and Modern type
        // and geometry stay on the same grid at 1920×1080 and 800×480.
        _loadoutV2Layout = frame.Layout(vp.Width, vp.Height,
            ModernHudScale.Core(1f, _hudSettings.Size, vp.Height), router);
        var hover = router.HoverIn(_loadoutV2Layout);
        DrawLoadoutPreview(hangar, vp, _loadoutV2Layout.ShipPreview);
        _loadoutV2Renderer!.Draw(frame, _loadoutV2Layout, _hudSettings.Style, hover);
        DrawHangarStatus(hangar, vp);
        hangar.Apply(result.Request, world, own, readiness, missionStarted);
    }

    /// <summary>The loadout screen's hull preview pane (<c>Game1.LoadoutV2.cs:104-140</c>):
    /// native's spin and framing over the hull's real mesh. A hull whose mesh has not
    /// arrived streams through the P1065 battle pack (<c>RequestBattlePack("hull-N")</c>)
    /// and shows the same stand-in the flight pass uses meanwhile; the join manifest is
    /// untouched.</summary>
    private void DrawLoadoutPreview(BrowserHangarModel hangar, Viewport vp, TouchRect preview)
    {
        if (preview.Width < 4 || preview.Height < 4) return;
        short hullId = hangar.Shop?.EffectiveHullId ?? -1;
        if (hullId < 0) return;
        RequestBattlePack?.Invoke("hull-" + hullId);
        _hangarPreviewSpin = DockedHullOrbit.Advance(_hangarPreviewSpin, _flightElapsedSeconds);

        var saved = vp;
        var rect = new Viewport((int)preview.X, (int)preview.Y,
            Math.Max(1, (int)preview.Width), Math.Max(1, (int)preview.Height));
        GraphicsDevice.Viewport = rect;
        try
        {
            // Native gives the loadout geo its own z-buffered pane with no world scene
            // (load.cpp:860-910); Game1.Rendering.cs:5140-5150 clears and re-enables
            // opaque depth for exactly this reason.
            GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            var hull = _battleScene?.Hull(hullId)
                ?? _shopCatalog?.Hulls.Where(h => h.HullId == hullId).Select(h => (DataHullTypeIGC?)h).FirstOrDefault();
            var world = DockedHullOrbit.World(_hangarPreviewSpin);
            if (hull is { } h && _battleScene?.MeshBytesReady(hullId,
                    (Allegiance.ClientCore.Browser.Assets.StagedAssetSource)_assets) == true
                && _flightShips?.MeshFor(h.ModelName) is { } mesh)
            {
                string? texture = _flightShips.TextureStemFor(h.ModelName, h.TextureName);
                var texture2D = texture is null ? null : _flightShips.TextureFor(texture);
                float radius = NativeShipResolver.NativeScaleForLength(h.Length, mesh.MeshRadius) * mesh.MeshRadius;
                var view = Matrix.CreateLookAt(new Vector3(0, 0, DockedHullPreviewFraming.EyeDistance(radius)),
                    Vector3.Zero, Vector3.Up);
                var projection = Matrix.CreatePerspectiveFieldOfView(DockedHullPreviewFraming.FovRadians,
                    rect.AspectRatio, DockedHullPreviewFraming.NativeZClipNear, DockedHullPreviewFraming.NativeZClipFar);
                _flightShips.Draw(mesh, texture2D, world, view, projection, 1f,
                    NativeShipResolver.NativeScaleForLength(h.Length, mesh.MeshRadius),
                    _sceneLighting, h.ModelName, texture, false);
            }
            else
            {
                // P1065 stand-in: the same lit box the flight pass draws for a hull
                // whose verified mesh bytes have not landed yet.
                float side = BattleShipScene.StandInSide(hull?.Length ?? 2);
                var view = Matrix.CreateLookAt(new Vector3(0, 0, DockedHullPreviewFraming.EyeDistance(1f)),
                    Vector3.Zero, Vector3.Up);
                var projection = Matrix.CreatePerspectiveFieldOfView(DockedHullPreviewFraming.FovRadians,
                    rect.AspectRatio, 0.1f, 40f);
                _flightBolts?.Draw(Matrix.CreateScale(side) * world, new Vector3(.6f, .8f, 1f),
                    view, projection);
            }
        }
        finally { GraphicsDevice.Viewport = saved; }
    }

    /// <summary>Edge helper for the docked keys — the desktop's
    /// <c>Edge(action)</c> (<c>Game1.Flight.cs:275</c>) over the held-key snapshots.</summary>
    private static bool Edge(KeyboardState now, KeyboardState then, string action)
        => ActionDownStatic(now, action) && !ActionDownStatic(then, action);

    private static bool ActionDownStatic(KeyboardState keys, string action)
        => KeyBindingStore.Instance.Map.IsDownMostSpecific(keys, action);
}
