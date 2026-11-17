using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly EffectLights _effectLights = new();
    private bool _allowSoft = true;
    private bool? _bloomOverride;
    private readonly List<Allegiance.ClientCore.State.HullHitEntity> _lightImpacts = new();
    private void ApplyEffectsDetail(Allegiance.ClientCore.State.EffectsDetail detail)
    {
        if (!Enum.IsDefined(detail)) detail = Allegiance.ClientCore.State.EffectsDetail.Medium;
        _upscaling.Effects = detail;
        _effectLights.Detail = _enhancedVfx ? detail : Allegiance.ClientCore.State.EffectsDetail.Low;
        _effectLights.Clear();
        _lightImpacts.Clear();
        _softEnabled = _enhancedVfx && _allowSoft && detail != Allegiance.ClientCore.State.EffectsDetail.Low;
        _bloomEnabled = _enhancedVfx && detail != Allegiance.ClientCore.State.EffectsDetail.Low &&
            (_bloomOverride ?? detail == Allegiance.ClientCore.State.EffectsDetail.High);
        if (_post?.Bloom is { } bloom) bloom.Polished = detail != Allegiance.ClientCore.State.EffectsDetail.Low;
    }
    private readonly HudSettings _hudSettings = HudSettings.Load(HudSettings.DefaultPath,
        File.Exists(IntroPreferences.FilePath) || File.Exists(UpscalingSettings.DefaultPath) || File.Exists(KeyBindingStore.ResolvePath()));
    private HudSkinSwitcher? _hudSkins;
    /// <summary>P1041 — the Jet palette registry (green + amber, both preloaded).</summary>
    private JetPaletteSwitcher? _jetPalettes;
    private IHudSkinRenderer ActiveHud => _hudSkins!.Active.Renderer;
    private bool _hudSkinKeysRead;
    private KeyboardState _hudSkinKeys;
    private string? _hudSkinNotice;
    private double _hudSkinNoticeUntil;

    private void InitializeHudSkins()
    {
        var resources = new HudSkinResources(
            model => _radarRenderer.Draw(model, GraphicsDevice.Viewport),
            DrawHudShipTags, DrawEjectDeathOverlays, DrawCommandHud,
            DrawChat, DrawCargoHud, DrawFlightPartsHud)
        {
            // P1040 — Jet draws its own CMD console; it still gets the native
            // command-camera chrome (NORTH + drag rectangle) through this pass.
            CommandChrome = DrawCommandChrome
        };
        var classic = new HudSkinRenderer(_hudRenderer, resources, _radarRenderer.SetRadarIcons);
        var modern = new HudSkinRenderer(_modernHudRenderer, resources);
        // P1035 — Jet is installed (preloaded) but gated behind hud.jetSkin: the
        // switcher skips it in the cycle and refuses Request("jet") while the flag
        // is off. Its shared passes are the Classic composition (see the renderer).
        // P1041 — BOTH palette variants are preloaded here (green + amber), so a
        // variant switch is a reference swap at the frame boundary, never a load,
        // parse or allocation during Draw (P972 §2.5).
        IHudSkinRenderer? jet = null;
        if (HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet) is { } jetDefinition)
        {
            var amberDefinition = HudSkinResolver.Catalog.TryGetVariant(HudSkinIds.Jet, JetPaletteIds.Amber)
                ?? jetDefinition;
            var green = new Rendering.Jet.JetHudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig, _hudSettings,
                resources, jetDefinition, _displayScale, _radarRenderer.SetRadarIcons);
            var amber = new Rendering.Jet.JetHudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig, _hudSettings,
                resources, amberDefinition, _displayScale, _radarRenderer.SetRadarIcons);
            _jetPalettes = new JetPaletteSwitcher(_hudSettings,
                new ResolvedJetPalette(jetDefinition, green), new ResolvedJetPalette(amberDefinition, amber));
            jet = new JetPaletteRenderer(_jetPalettes);
        }
        _hudSkins = new(_hudSettings, classic, modern, jet, jetEnabled: JetSkinEnabled);
        _hudSkins.Active.Renderer.SetRadarIcons(_radarIconTextures);
    }

    /// <summary>P1035 — the live <c>hud.jetSkin</c> gate. Read at the update
    /// boundary; never constructs anything.</summary>
    private bool JetSkinEnabled() => _visuals?.JetSkin == true;

    private void UpdateHudSkinInput(KeyboardState keys, bool focused)
    {
        if (_hudSkins is null) return;
        // P1035 — toggling hud.jetSkin off while Jet is active queues Modern at
        // this boundary; CommitHudSkinChange then publishes it atomically.
        _hudSkins.ObserveJetAvailability(JetSkinEnabled());
        var map = KeyBindingStore.Instance.Map;
        // This port-only action has one rebindable chord and no aliases in
        // either preset. Avoid IsDown's temporary alternate-binding list.
        bool down = map.BindingOf("CycleHudSkin").Matches(keys);
        bool blocked = !down || !focused || _chatActive || _qcMenuOpen || _graphicsSettingsVisible
            || _keyBindVisible || _mainMenuVisible || _menuConsumedFrame || _helpVisible
            || _creditsVisible || _playerPingsVisible || _pilotProfile.Editing
            || _teamNameEditing || _civPickerOpen || _settingsForm is not null
            || _trainingQuitConfirm || _hangarQuitConfirm
            || _state is not (ClientState.Flying or ClientState.ReadyCheck)
            || !map.IsDownMostSpecific(keys, "CycleHudSkin");
        _hudSkins.ObserveCycle(down, blocked);
    }

    private void CommitHudSkinChange()
    {
        // P1041 — a palette request is published at the same boundary as a skin
        // request; either change saves and announces exactly once.
        bool skinChanged = _hudSkins?.ApplyPending() == true;
        bool paletteChanged = _jetPalettes?.ApplyPending() == true;
        if (!skinChanged && !paletteChanged) return;
        SaveHudSettingsQuietly(); // activation succeeded; now persist and announce
        _hudSkinNotice = HudSkinNotice.Format(_hudSkins!.Active.Definition.Name, ActiveJetPalette());
        _hudSkinNoticeUntil = _nowSeconds + 2;
    }

    /// <summary>P1041 — the palette shown in the toast/options row, or null when a
    /// skin without variants is active (then the toast is the plain skin name).</summary>
    private string? ActiveJetPalette()
        => _hudSkins is { } skins && skins.Active.Renderer.Id == HudSkinIds.Jet ? _jetPalettes?.Active.Id : null;

    /// <summary>P1041 — the Options screen's variant row is present only while Jet
    /// is active and the gate is on; with <c>hud.jetSkin</c> off it is HIDDEN (not
    /// a disabled row) and the stored palette is kept.</summary>
    private bool JetPaletteRowVisible()
        => JetPaletteRow.Visible(_hudSkins?.Active.Renderer.Id, JetSkinEnabled());

    /// <summary>P1035 — the Ctrl+F6 HUD-skin row (graphics row 6). One function so
    /// the options test can assert the active skin's name — including Jet — is what
    /// the row lists, without duplicating the panel's literal.</summary>
    internal static string HudStyleRowLabel(string name) => "HUD style: " + name;
    private readonly UpscalingSettings _upscaling = UpscalingSettings.Load(UpscalingSettings.DefaultPath);
    private readonly DynamicResolutionController _resolutionController = new();
    private readonly FlightGcLatency _flightGc = new(Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_GC_LOWLATENCY") == "1");
    private ClientFrameTelemetry? _frameTelemetry;
    private GlFrameTimer? _gpuTimer;
    private FsrPostProcessor? _fsr;
    private bool _graphicsSettingsVisible, _graphicsOpenWasDown;
    /// <summary>P805 (row 470) — native ShowChatHistoryHUD toggle (menu-only,
    /// NOT persisted: WinTrek.cpp:2897,5074-5078,3934), default on.</summary>
    private bool _chatHistoryVisible = true;
    private KeyboardState _graphicsPrevious;
    private int _graphicsRow, _graphicsFirstRow;
    /// <summary>P828 — the number of rows in the Ctrl+F6 settings panel (13 → 16
    /// with P805's Center/Target HUD + Chat History, 16 → 19 with the radar label
    /// declutter rows, 19 → 20 with the P836 HUD level row, 20 → 21 with native's
    /// Debris cycle, 21 → 22 with P897 transport, 22 → 24 with P948's persisted
    /// mouse options). Up/Down wrap on it. P1041 — this is the BASE count: the Jet
    /// palette row exists only while Jet is active, so the live count is 25 with it
    /// and 24 without (<see cref="PanelRowCount"/>); the constant stays 24 so the
    /// source-inspection tests keep pinning the flag-off list.</summary>
    private const int GraphicsRowCount = 24;
    /// <summary>P1041 — the variant row's panel position: directly under the HUD
    /// style row (6), "next to" the existing skin control (P972 §2.5).</summary>
    private const int GraphicsRowPalette = 7;
    /// <summary>P1041 — the panel's live row count (the variant row appears only
    /// while Jet is the active skin with the gate on).</summary>
    private int PanelRowCount => JetPaletteRowVisible() ? GraphicsRowCount + 1 : GraphicsRowCount;
    /// <summary>P1041 — map a semantic row (the pre-P1041 index) to its panel row:
    /// the variant row inserted at 7 shifts every later row down by one.</summary>
    private int GraphicsRowFor(int semantic) =>
        JetPaletteRowVisible() && semantic >= GraphicsRowPalette ? semantic + 1 : semantic;
    /// <summary>P948 — the panel rows for the persisted mouse options (native
    /// FlipY and MouseSensitivity); the P948 real-window proof selects them.</summary>
    private const int GraphicsRowInvertMouseY = 22;
    private const int GraphicsRowMouseSensitivity = 23;
    private Action<long, bool, double>? _receiveGpu;
    private long _budgetStart;
    private double _budgetDuration;
    private string? _budgetCapture;
    private long _resolutionGpuGeneration;
    private int _postWidth, _postHeight;
    private bool _scaledThisFrame;

    private void InitializeGpuBudget()
    {
        try { _hudSettings.Save(HudSettings.DefaultPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[hud2] cannot save HUD preferences: {e.Message}"); }
        _nativeShipRenderer.HdTexturesEnabled = _upscaling.HdTextures;
        _nativeShipRenderer.HdPbrEnabled = _upscaling.HdPbr;
        SyncHdMaterialMode();
        _hdMaterials.Flashes = _effectLights;
        _fsr = new(GraphicsDevice, _shaders!);
        _postWidth = GraphicsDevice.PresentationParameters.BackBufferWidth;
        _postHeight = GraphicsDevice.PresentationParameters.BackBufferHeight;
        _budgetStart = Stopwatch.GetTimestamp();
        double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_PERF_SECONDS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _budgetDuration);
        _budgetCapture = Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_PERF_CAPTURE");
        _resolutionController.Reset(UpscalingSettings.Scale(_upscaling.Preset));
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_PERF") == "1" || _upscaling.DynamicResolution)
            EnableFrameTelemetry();
    }
    /// <summary>P836 — persist hud.json from the in-game cycle key without
    /// surfacing an error (same swallow shape as the settings panel).</summary>
    private void SaveHudSettingsQuietly()
    {
        try { _hudSettings.Save(HudSettings.DefaultPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[hud2] cannot save HUD preferences: {e.Message}"); }
    }

    /// <summary>HD-5 — push the two player settings (HD textures, experimental HD
    /// lighting) into the shared <see cref="HdMaterialRenderer.Mode"/> so every
    /// renderer follows one coherent pack mode. Null-safe for menu unit tests that
    /// drive the menu before the renderer material is constructed.</summary>
    private void SyncHdMaterialMode()
    {
        if (_hdMaterials is not null)
            _hdMaterials.Mode = HdMaterialRenderer.ModeFor(_upscaling.HdTextures, _upscaling.HdPbr);
    }
    private void EnableFrameTelemetry()
    {
        if (_frameTelemetry is not null) return;
        _frameTelemetry = new(Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_PERF_CSV"));
        _receiveGpu = _frameTelemetry.ReceiveGpu;
        _gpuTimer = GlFrameTimer.TryCreate();
        _frameTelemetry.GpuAvailable = _gpuTimer is not null;
        Console.WriteLine($"[p643] GPU timer: {(_gpuTimer is null ? "unavailable (present wait is CPU blocking, not GPU time)" : "GL_TIME_ELAPSED scene/post, asynchronous")}");
    }
    private bool PrepareUpscaling(int width, int height)
    {
        bool enabled = _upscaling.Upscaling == UpscalingMode.FSR1 && _fsr is { Ready: true };
        float maximum = UpscalingSettings.Scale(_upscaling.Preset);
        float scale = maximum;
        if (enabled && _upscaling.DynamicResolution)
        {
            EnableFrameTelemetry();
            if (_frameTelemetry!.GpuGeneration != _resolutionGpuGeneration)
            {
                _resolutionGpuGeneration = _frameTelemetry.GpuGeneration;
                _resolutionController.Observe(_frameTelemetry.LatestGpuMs, _upscaling.TargetFrameMs, maximum);
            }
            scale = _resolutionController.CurrentScale;
        }
        var dimensions = enabled ? UpscalingSettings.Dimensions(width, height, scale) : (width, height);
        if (_postWidth != dimensions.Item1 || _postHeight != dimensions.Item2)
        {
            _post?.SetSize(dimensions.Item1, dimensions.Item2);
            _postWidth = dimensions.Item1; _postHeight = dimensions.Item2;
            SyncHdrState();
        }
        // P984 — the FSR targets follow the scene target's format (RGBA16F on the
        // HDR path), so the upscale cannot clip the float scene.
        if (enabled) _fsr!.SetSize(width, height, scale, HdrScene);
        _scaledThisFrame = enabled;
        return enabled;
    }
    /// <summary>P828 — the settings panel's height: header + one 46 px row per
    /// option + footer, so the 19 rows (up from 16) stay inside the panel. Pure
    /// so a test can pin that the panel outgrew the old fixed 700 px.</summary>
    internal static int GraphicsPanelHeight(int rows) => 60 + 46 * rows + 30;
    private bool UpdateGraphicsSettings(KeyboardState ks)
    {
        bool open = KeyOf("OpenGraphicsSettings").Matches(ks);
        if (open && !_graphicsOpenWasDown && !_chatActive && !_qcMenuOpen) _graphicsSettingsVisible = !_graphicsSettingsVisible;
        _graphicsOpenWasDown = open;
        if (!_graphicsSettingsVisible) { _graphicsPrevious = ks; return false; }
        _flightGc.SetFlying(false);
        bool Press(Keys k) => ks.IsKeyDown(k) && !_graphicsPrevious.IsKeyDown(k);
        if (Press(Keys.Escape)) _graphicsSettingsVisible = false;
        // P1041 — the panel's row count changes when the Jet variant row appears
        // or disappears (skin/gate changed at the frame boundary); keep the
        // cursor inside it.
        _graphicsRow = Math.Clamp(_graphicsRow, 0, PanelRowCount - 1);
        if (Press(Keys.Down)) _graphicsRow = (_graphicsRow + 1) % PanelRowCount;
        if (Press(Keys.Up)) _graphicsRow = (_graphicsRow + PanelRowCount - 1) % PanelRowCount;
        int direction = Press(Keys.Right) ? 1 : Press(Keys.Left) ? -1 : 0;
        bool changed = direction != 0;
        bool paletteRow = JetPaletteRowVisible();
        int semanticRow = paletteRow && _graphicsRow > GraphicsRowPalette ? _graphicsRow - 1 : _graphicsRow;
        if (changed && paletteRow && _graphicsRow == GraphicsRowPalette)
        {
            // P1041 — the variant selector, next to the HUD style row and with the
            // same Left/Right behaviour the skin row uses. The request is queued
            // and published at the update/draw boundary (CommitHudSkinChange),
            // which saves and shows `SKIN: Jet / amber`.
            if (_jetPalettes is not null) _hudSettings.JetPalette = JetPaletteIds.Next(_jetPalettes.Active.Id);
            changed = false;
        }
        else if (changed)
        {
            switch (semanticRow)
            {
                case 9: ApplyEffectsDetail((Allegiance.ClientCore.State.EffectsDetail)(((int)_upscaling.Effects + direction + 3) % 3)); break;
                case 6: _hudSkins!.Cycle(direction); changed = false; break; // save after activation
                case 7: _hudSettings.Size = Math.Clamp(_hudSettings.Size + direction * .05f, .75f, 1.5f); break;
                case 8: _hudSettings.ColorBlindSafe = !_hudSettings.ColorBlindSafe; break;
                case 11: _hudSettings.RoundRadar = !_hudSettings.RoundRadar; break;
                // HUD-19 — native Graphics Options "Joystick Indicator: On/Off"
                // (WinTrek.cpp:4507,4526), toggled/saved at WinTrek.cpp:5222-5238.
                case 12: _hudSettings.ShowJoystickIndicator = !_hudSettings.ShowJoystickIndicator; break;
                // P805 (row 470) — native Graphics Options toggles: Center HUD
                // (idmToggleCenterHUD 615, persisted WinTrek.cpp:5082-5086), Target
                // HUD (616, :5090-5094) and the menu-only Chat History (614,
                // :5074-5078, NOT persisted).
                case 13: _hudSettings.ShowCenterHud = !_hudSettings.ShowCenterHud; break;
                case 14: _hudSettings.ShowTargetHud = !_hudSettings.ShowTargetHud; break;
                case 15: _chatHistoryVisible = !_chatHistoryVisible; break;
                // P828 — the Modern-HUD radar label declutter: on/off, and the
                // nearest-N ordinary label budget (left/right steps by 2).
                case 16: _hudSettings.RadarDeclutter = !_hudSettings.RadarDeclutterOn; break;
                case 17: _hudSettings.RadarLabelMax = Math.Clamp(_hudSettings.RadarLabelMax + direction * 2, 0, RadarLabelDeclutter.MaxExtraLabelsLimit); break;
                case 18: _hudSettings.RadarLabelTelemetry = !_hudSettings.RadarLabelTelemetry; break;
                // P836 — the Modern-HUD density level (Classic ignores it). Left/
                // right step through the five settable levels (Full/Tactical/
                // Combat/Cinematic/Auto). Auto resolves from the in-sector ship
                // count and never selects Cinematic.
                case 19: _hudSettings.Level = Rendering.HudLevels.Step(_hudSettings.Level, direction); break;
                // Modern transport preference: apply at the next connection.
                case 21:
                    _hudSettings.Transport = (Discovery.ClientTransportSetting)(((int)_hudSettings.Transport + direction + 3) % 3);
                    break;
                // P948 — the persisted mouse options (player.json), the same live
                // controls native's Game Options expose: FlipY ('Y',
                // WinTrek.cpp:4536/5297-5305) and Mouse Options' Raise/Lower
                // Sensitivity (:4612-4613/5512-5531). Left/Right toggles or steps
                // native's 0.01; each change saves immediately.
                case GraphicsRowInvertMouseY:
                    SetInvertMouseY(!_mouseFlight.InvertPitch);
                    break;
                case GraphicsRowMouseSensitivity:
                    StepMouseSensitivity(direction);
                    break;
                // Native Graphics Options "Debris Low/Medium/High/Off" (idmToggleDebris 'D',
                // WinTrek.cpp:4511,4633-4653): one cycle step per press, saved, applied live.
                case 20:
                    _upscaling.Debris = Rendering.DebrisGeo.NextDensity(_debrisGeo.Density);
                    _debrisGeo.Density = _upscaling.Debris;
                    Console.WriteLine($"[p643] {Rendering.DebrisGeo.MenuLabel(_upscaling.Debris)}");
                    break;
                case 0: _upscaling.Upscaling = _upscaling.Upscaling == UpscalingMode.Off ? UpscalingMode.FSR1 : UpscalingMode.Off; break;
                case 1: _upscaling.Preset = (FsrPreset)(((int)_upscaling.Preset + direction + 4) % 4); break;
                case 2: _upscaling.Sharpness = Math.Clamp(_upscaling.Sharpness + direction * .05f, 0, 1); break;
                case 3: _upscaling.DynamicResolution = !_upscaling.DynamicResolution; break;
                case 5: _upscaling.HdTextures = !_upscaling.HdTextures; _nativeShipRenderer.HdTexturesEnabled = _upscaling.HdTextures; SyncHdMaterialMode(); break;
                case 10: _upscaling.HdPbr = !_upscaling.HdPbr; _nativeShipRenderer.HdPbrEnabled = _upscaling.HdPbr; SyncHdMaterialMode(); break;
                case 4: _upscaling.TargetFrameMs = Math.Clamp(_upscaling.TargetFrameMs + direction, 8, 100); break;
            }
        }
        var mouse = Mouse.GetState();
        var slider = new Rectangle(52, 198, 300, 14);
        if (_graphicsFirstRow == 0 && mouse.LeftButton == ButtonState.Pressed && slider.Contains(mouse.Position))
        {
            _graphicsRow = 2;
            float sharpness = Math.Clamp((mouse.X - slider.X) / (float)slider.Width, 0, 1);
            changed |= sharpness != _upscaling.Sharpness;
            _upscaling.Sharpness = sharpness;
        }
        // P1041 — the HUD-size slider follows the size row when the Jet variant
        // row shifts it down one slot.
        var hudSlider = HudSizeSliderRect();
        if (_graphicsFirstRow == 0 && mouse.LeftButton == ButtonState.Pressed && hudSlider.Contains(mouse.Position))
        {
            _graphicsRow = GraphicsRowFor(7); changed = true;
            _hudSettings.Size = .75f + .75f * Math.Clamp((mouse.X - hudSlider.X) / (float)hudSlider.Width, 0, 1);
        }
        if (changed)
        {
            if (_upscaling.DynamicResolution) EnableFrameTelemetry();
            _resolutionController.Reset(UpscalingSettings.Scale(_upscaling.Preset));
            try { _hudSettings.Save(HudSettings.DefaultPath); _upscaling.Save(UpscalingSettings.DefaultPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[p643] cannot save graphics settings: {e.Message}"); }
        }
        _graphicsPrevious = ks;
        return true;
    }
    /// <summary>P1041 proof — the panel row list is logged once per open (only under
    /// the proof env), so the "Options unchanged" check compares TEXT.</summary>
    private bool _panelRowsLogged;
    /// <summary>P1041 — the HUD-size slider's panel rectangle (the row of the
    /// "HUD size" line, shifted by the Jet variant row when it is present).</summary>
    private Rectangle HudSizeSliderRect() => new(360, 78 + GraphicsRowFor(7) * 46 + 30, 260, 8);
    private void DrawGpuBudgetOverlay()
    {
        // P989 — an evidence capture (ALLEGIANCE_BENCHMARK_FRAME, or the wall-clock
        // ALLEGIANCE_CLIENT_PERF_CAPTURE) must be the real frame, not the benchmark's
        // own telemetry overlay drawn over it.
        if (_budgetCapture is not null) return;
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_FRAME") is { Length: > 0 }) return;
        if (!_graphicsSettingsVisible && _frameTelemetry is null) return;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        if (_graphicsSettingsVisible)
        {
            var rows = new List<string>(PanelRowCount) {
                $"Upscaling: {_upscaling.Upscaling}",
                $"Preset: {_upscaling.Preset} ({UpscalingSettings.Scale(_upscaling.Preset):P0})",
                $"RCAS sharpness: {_upscaling.Sharpness:P0}",
                $"Dynamic resolution: {(_upscaling.DynamicResolution ? "On" : "Off")}",
                $"Target frame time: {_upscaling.TargetFrameMs:F1} ms",
                $"HD textures: {(_upscaling.HdTextures ? "On" : "Off")} ({_nativeShipRenderer.HdResidentBytes / 1048576.0:F1} MiB resident)",
                HudStyleRowLabel(_hudSkins!.Active.Definition.Name),
            };
            // P1041 — the Jet variant selector sits directly under the skin row and
            // exists only while Jet is active with hud.jetSkin on (hidden, not
            // disabled, on Classic/Modern or with the gate off; the stored palette
            // is kept either way).
            if (JetPaletteRowVisible()) rows.Add(JetPaletteRow.Label(_jetPalettes?.Active.Id));
            rows.AddRange(new[] {
                $"HUD size: {_hudSettings.Size:P0}",
                $"Colour-blind safe palette: {(_hudSettings.ColorBlindSafe ? "On" : "Off")}",
                $"Effects quality: {_upscaling.Effects}",
                $"HD lighting (PBR, experimental): {(_upscaling.HdPbr ? "On" : "Off")}",
                $"Round radar border: {(_hudSettings.RoundRadar ? "On" : "Off")}",
                $"Joystick indicator: {(_hudSettings.ShowJoystickIndicator ? "On" : "Off")}",
                $"Center HUD: {(_hudSettings.ShowCenterHud ? "On" : "Off")}",
                $"Target HUD: {(_hudSettings.ShowTargetHud ? "On" : "Off")}",
                $"Chat history: {(_chatHistoryVisible ? "On" : "Off")}",
                // P828 (Modern-HUD visual option; Classic stays exactly native).
                $"Radar label declutter: {(_hudSettings.RadarDeclutterOn ? "On" : "Off")} (Modern default)",
                $"Radar labels kept: {_hudSettings.RadarLabelMax} (nearest, after priority)",
                $"Radar label telemetry: {(_hudSettings.RadarLabelTelemetry ? "On" : "Off")}",
                // P836 — the Modern-HUD density level (Full/Tactical/Combat/
                // Cinematic/Auto; Classic ignores it). Shows the resolved level
                // too when Auto is active.
                $"HUD level: {_hudSettings.Level}{( _hudSettings.Level == Rendering.HudLevel.Auto ? $" → {_hudSettings.ActiveLevel}" : "")}",
                Rendering.DebrisGeo.MenuLabel(_debrisGeo.Density),
                $"Multiplayer transport: {_hudSettings.Transport} (next connection); active: {_session.ActiveTransportProtocol.ToUpperInvariant()}",
                // P948 — persisted mouse options (player.json): the invert-Y toggle
                // and the sensitivity row native's Game Options carry.
                $"Invert mouse Y: {(_mouseFlight.InvertPitch ? "On" : "Off")}",
                $"Mouse sensitivity: {Flight.MouseOptions.Percent(_mouseFlight.MouseSensitivity)}" });
            // P1041 proof — log the exact row list once per open so the "options screen
            // unchanged with hud.jetSkin off" check compares TEXT, not a translucent
            // panel's live-scene bleed. Never printed outside the proof env.
            if (Environment.GetEnvironmentVariable("ALLEGIANCE_P1041_PROOF") == "1" && !_panelRowsLogged)
            {
                _panelRowsLogged = true;
                Console.WriteLine("[hud.panel] " + string.Join(" | ", rows));
            }
            int panelHeight = Math.Min(GraphicsPanelHeight(rows.Count), GraphicsDevice.Viewport.Height - 64);
            _spriteBatch.Draw(_whitePixel, new Rectangle(32, 32, Math.Min(760, GraphicsDevice.Viewport.Width - 48), panelHeight), new Color(5, 15, 22, 245));
            _spriteBatch.DrawString(_hudSmall, "Settings - Graphics / HUD / Mouse (Ctrl+F6); arrows change, Esc closes", new Vector2(48, 44), Color.White);
            int visibleRows = Math.Max(1, (panelHeight - 105) / 46);
            int firstRow = Math.Clamp(_graphicsRow - visibleRows + 1, 0, Math.Max(0, rows.Count - visibleRows));
            _graphicsFirstRow = firstRow;
            for (int i = firstRow; i < Math.Min(rows.Count, firstRow + visibleRows); i++)
                _spriteBatch.DrawString(_hudSmall, (_graphicsRow == i ? "> " : "  ") + rows[i], new Vector2(48, 78 + (i - firstRow) * 46), _graphicsRow == i ? Color.Yellow : Color.White);
            if (firstRow == 0)
            {
                _spriteBatch.Draw(_whitePixel, new Rectangle(52, 198, 300, 8), Color.Gray);
                _spriteBatch.Draw(_whitePixel, new Rectangle(52, 198, (int)(300 * _upscaling.Sharpness), 8), Color.Cyan);
                Rectangle hudSlider = HudSizeSliderRect();
                _spriteBatch.Draw(_whitePixel, hudSlider, Color.Gray);
                _spriteBatch.Draw(_whitePixel, new Rectangle(hudSlider.X, hudSlider.Y, (int)(hudSlider.Width * (_hudSettings.Size - .75f) / .75f), hudSlider.Height), Color.Cyan);
            }
            _spriteBatch.DrawString(_hudSmall, _fsr is { Ready: true } ? "FSR1 ready; HUD stays at native resolution" : "FSR1 unavailable: using native resolution", new Vector2(48, 32 + panelHeight - 30), Color.White);
        }
        if (_frameTelemetry is not null)
        {
            int y = Math.Max(0, GraphicsDevice.Viewport.Height - 92);
            _spriteBatch.Draw(_whitePixel, new Rectangle(0, y, GraphicsDevice.Viewport.Width, 92), new Color(0, 0, 0, 205));
            _spriteBatch.DrawString(_hudSmall, _frameTelemetry.Overlay, new Vector2(8, y), Color.White);
        }
        _spriteBatch.End();
    }
    protected override void EndDraw()
    {
        if (_benchResult is null) DrawGpuBudgetOverlay();
        // P948 — the settings panel is drawn in EndDraw (after Draw's ordinary
        // autoshot flush), so the mouse-option proof latches its shot and flushes
        // it here, once the panel is really on the backbuffer.
        FlushP948ProofShot();
        // P1041 — the Jet palette proof's panel frames (drawn after this point).
        FlushP1041ProofShot();
        if (_benchExitAfterDraw && _benchResult is not null && Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            var parameters = GraphicsDevice.PresentationParameters;
            var pixels = new Color[parameters.BackBufferWidth * parameters.BackBufferHeight];
            GraphicsDevice.GetBackBufferData(pixels);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
            File.WriteAllBytes(screenshot, Capture.CapturePngSaver.Encode(pixels, parameters.BackBufferWidth, parameters.BackBufferHeight));
        }
        // P989 — deterministic replay-frame capture for A/B evidence. A wall-clock
        // capture (ALLEGIANCE_CLIENT_PERF_CAPTURE above) lands on a DIFFERENT replay
        // frame per run, because the frame cost changes with the pipeline under test;
        // the replay frame NUMBER is the only stable key, so this hook fires once at
        // or just past ALLEGIANCE_BENCHMARK_FRAME and writes a VALID PNG. Inert unless
        // both env vars are set (same class of hook as ALLEGIANCE_BENCHMARK_SCREENSHOT).
        var frameShot = _benchReplay;
        if (frameShot is not null && !_benchFrameShotDone
            && int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_FRAME"), out int wantFrame)
            && Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_FRAME_PNG") is { Length: > 0 } frameShotPath)
        {
            if (frameShot.Frame >= wantFrame)
            {
                _benchFrameShotDone = true;
                var shotParameters = GraphicsDevice.PresentationParameters;
                var shotPixels = new Color[shotParameters.BackBufferWidth * shotParameters.BackBufferHeight];
                GraphicsDevice.GetBackBufferData(shotPixels);
                using var shotTexture = new Texture2D(GraphicsDevice, shotParameters.BackBufferWidth, shotParameters.BackBufferHeight);
                shotTexture.SetData(shotPixels);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(frameShotPath))!);
                using (var shotFile = File.Create(frameShotPath))
                    shotTexture.SaveAsPng(shotFile, shotTexture.Width, shotTexture.Height);
                Console.WriteLine($"[capture] replay frame {frameShot.Frame} (wanted {wantFrame}) -> {frameShotPath}");
                // The shot is the whole point of the run: stop here instead of playing
                // the rest of the replay to its end.
                Exit();
            }
        }
        if (_budgetDuration > 0 && Stopwatch.GetElapsedTime(_budgetStart).TotalSeconds >= _budgetDuration)
        {
            if (!string.IsNullOrWhiteSpace(_budgetCapture))
            {
                var ppCapture = GraphicsDevice.PresentationParameters;
                var pixels = new Color[ppCapture.BackBufferWidth * ppCapture.BackBufferHeight];
                GraphicsDevice.GetBackBufferData(pixels);
                using var texture = new Texture2D(GraphicsDevice, ppCapture.BackBufferWidth, ppCapture.BackBufferHeight);
                texture.SetData(pixels);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_budgetCapture))!);
                using var file = File.Create(_budgetCapture);
                texture.SaveAsPng(file, texture.Width, texture.Height);
            }
            _budgetDuration = 0;
            Exit();
        }
        _frameTelemetry?.Submitted();
        long draws = GraphicsDevice.Metrics.DrawCount;
        var pp = GraphicsDevice.PresentationParameters;
        long start = Stopwatch.GetTimestamp();
        base.EndDraw();
        _frameTelemetry?.EndDraw(Stopwatch.GetElapsedTime(start).TotalMilliseconds, draws,
            pp.BackBufferWidth, pp.BackBufferHeight, _scaledThisFrame ? _postWidth : pp.BackBufferWidth,
            _scaledThisFrame ? _postHeight : pp.BackBufferHeight);
        if (_benchExitAfterDraw && _benchResult is not null) Exit();
    }
}
