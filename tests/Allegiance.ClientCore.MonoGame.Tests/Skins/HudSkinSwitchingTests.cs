using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

[Collection("LiveNetwork")]
public sealed class HudSkinSwitchingTests(ITestOutputHelper output)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly KeyboardState Cycle = new(Keys.LeftShift, Keys.F12);

    [Theory]
    [InlineData(InputLayout.Port)]
    [InlineData(InputLayout.Native)]
    public void Both_presets_use_free_Shift_F12_with_specific_chord_precedence(InputLayout layout)
    {
        var map = new ActionMap(InputActionCatalog.Definitions, layout);
        Assert.Equal(new(Keys.F12, InputModifiers.Shift), map.BindingOf("CycleHudSkin"));
        Assert.Empty(map.AlternatesOf("CycleHudSkin")); // polling uses this single rebindable chord
        Assert.Single(InputLayouts.Defaults(layout), p => p.Value == map.BindingOf("CycleHudSkin"));
        var native = DefaultInputMap.ParseKeyMap(DefaultInputMap.EmbeddedText);
        Assert.DoesNotContain(native, b => b.Vk == 123 && b.Modifiers == InputModifiers.Shift);
        Assert.Contains(native, b => b.Vk == 123 && b.Modifiers == InputModifiers.Control && b.TkNumber == 203);
        Assert.True(map.IsDownMostSpecific(Cycle, "CycleHudSkin"));
        Assert.False(map.IsDownMostSpecific(Cycle, "Credits"));
        Assert.False(map.IsDownMostSpecific(Cycle, "ResetTMClockSpeed"));
        Assert.False(map.IsDownMostSpecific(new(Keys.F12), "CycleHudSkin"));
        Assert.True(map.IsDownMostSpecific(new(Keys.F12), "Credits"));
        Assert.True(map.IsDownMostSpecific(new(Keys.LeftControl, Keys.F12), "ResetTMClockSpeed"));
        Assert.Null(map.SetBinding("CycleHudSkin", new(Keys.F15, InputModifiers.Alt)));
        Assert.False(map.IsDownMostSpecific(Cycle, "CycleHudSkin"));
        Assert.True(map.IsDownMostSpecific(new(Keys.LeftAlt, Keys.F15), "CycleHudSkin"));
    }

    [Theory]
    [InlineData(HudSkinIds.Classic)]
    [InlineData(HudSkinIds.Modern)]
    public void Queue_is_atomic_and_held_press_fires_once_with_round_trip_restart(string initial)
    {
        using var h = new Harness(initial);
        var old = h.Switcher.Active;
        h.Input(Cycle);
        Assert.Same(old, h.Switcher.Active);
        Assert.Equal(initial, h.Settings.Skin);
        Assert.False(File.Exists(h.HudPath)); // requests never persist before activation
        h.Commit();
        string other = initial == HudSkinIds.Classic ? HudSkinIds.Modern : HudSkinIds.Classic;
        Assert.Equal(other, h.Switcher.Active.Definition.Id);
        Assert.Equal(other, HudSettings.Load(h.HudPath, true).Skin);
        for (int i = 0; i < 8; i++) { h.Input(Cycle); h.Commit(); }
        Assert.Equal(other, h.Settings.Skin);
        var restarted = HudSettings.Load(h.HudPath, true);
        using var next = new HudSkinSwitcher(restarted, new Stub(HudSkinIds.Classic), new Stub(HudSkinIds.Modern));
        Assert.Equal(other, next.Active.Definition.Id);
        h.Input(new()); h.Input(Cycle); h.Commit();
        Assert.Same(old, h.Switcher.Active);
        Assert.Equal(initial, HudSettings.Load(h.HudPath, true).Skin);
    }

    [Theory]
    [InlineData("_chatActive")]
    [InlineData("_qcMenuOpen")]
    [InlineData("_graphicsSettingsVisible")]
    [InlineData("_keyBindVisible")]
    [InlineData("_mainMenuVisible")]
    [InlineData("_menuConsumedFrame")]
    [InlineData("_helpVisible")]
    [InlineData("_creditsVisible")]
    [InlineData("_playerPingsVisible")]
    [InlineData("_teamNameEditing")]
    [InlineData("_civPickerOpen")]
    [InlineData("_trainingQuitConfirm")]
    [InlineData("_hangarQuitConfirm")]
    public void Compose_and_modal_ownership_consumes_held_edges(string field)
    {
        using var h = new Harness();
        h.Set(field, true);
        h.Input(Cycle); h.Commit();
        Assert.Equal(HudSkinIds.Classic, h.Settings.Skin);
        h.Set(field, false);
        h.Input(Cycle); h.Commit(); // modal went away but the key is still held
        Assert.Equal(HudSkinIds.Classic, h.Settings.Skin);
        h.Input(new()); h.Input(Cycle); h.Commit();
        Assert.Equal(HudSkinIds.Modern, h.Settings.Skin);
    }

    [Fact]
    public void Focus_and_training_action_filter_do_not_create_a_late_press()
    {
        using var h = new Harness();
        h.Input(Cycle, false); h.Commit();
        h.Input(Cycle); h.Commit();
        Assert.Equal(HudSkinIds.Classic, h.Settings.Skin);
        h.Input(new());
        var map = KeyBindingStore.Instance.Map;
        map.ActionFilter = action => action != "CycleHudSkin";
        h.Input(Cycle); h.Commit();
        map.ActionFilter = null;
        h.Input(Cycle); h.Commit();
        Assert.Equal(HudSkinIds.Classic, h.Settings.Skin);
        h.Input(new()); h.Input(Cycle); h.Commit();
        Assert.Equal(HudSkinIds.Modern, h.Settings.Skin);
    }

    [Theory]
    [InlineData(HudSkinIds.Classic)]
    [InlineData(HudSkinIds.Modern)]
    public void Options_switch_with_target_chat_panes_and_command_view_preserves_state(string initial)
    {
        using var h = new Harness(initial);
        var panes = new ConsolePaneState { Inventory = true, Nav = true, Team = TeamConsoleSize.Expanded };
        h.Set("_consolePanes", panes);
        h.Set("_cameraMode", CameraMode.Command);
        var target = new Game1.AutopilotTarget(Game1.TargetKind.Station, 42, "Target station");
        h.Set("_autoTarget", target);
        h.Set("_chatActive", true);
        var text = new System.Text.StringBuilder("a message in progress");
        h.Set("_chatBuffer", text);
        h.Set("_graphicsSettingsVisible", true); h.Set("_graphicsRow", 6);
        h.Call("UpdateGraphicsSettings", new KeyboardState(Keys.Right));
        Assert.Equal(initial, h.Settings.Skin);
        h.Commit();
        string other = initial == HudSkinIds.Classic ? HudSkinIds.Modern : HudSkinIds.Classic;
        Assert.Equal(other, h.Settings.Skin);
        Assert.Equal(panes, h.Get<ConsolePaneState>("_consolePanes"));
        Assert.Equal(CameraMode.Command, h.Get<CameraMode>("_cameraMode"));
        Assert.Equal(target, h.Get<Game1.AutopilotTarget?>("_autoTarget"));
        Assert.True(h.Get<bool>("_chatActive"));
        Assert.Equal("a message in progress", text.ToString());
        Assert.Same(text, h.Get<System.Text.StringBuilder>("_chatBuffer"));
        Assert.Equal(HudLevel.Combat, h.Settings.Level);
        Assert.False(h.Settings.SelectionBracketOn); // explicit player overrides survive
        Assert.False(h.Settings.RadarDeclutterOn);
        h.Call("UpdateGraphicsSettings", new KeyboardState(Keys.Right)); h.Commit();
        Assert.Equal(other, h.Settings.Skin);
        h.Call("UpdateGraphicsSettings", new KeyboardState());
        h.Call("UpdateGraphicsSettings", new KeyboardState(Keys.Left)); h.Commit();
        Assert.Equal(initial, h.Settings.Skin);
        Assert.Equal(initial, HudSettings.Load(h.HudPath, true).Skin);
    }

    [Theory]
    [InlineData(HudSkinIds.Classic, false)]
    [InlineData(HudSkinIds.Modern, false)]
    [InlineData(HudSkinIds.Classic, true)]
    [InlineData(HudSkinIds.Modern, true)]
    public void Training_exit_restores_active_renderer_and_persisted_skin(string initial, bool queuedAtEntry)
    {
        string other = initial == HudSkinIds.Classic ? HudSkinIds.Modern : HudSkinIds.Classic;
        using var h = new Harness(queuedAtEntry ? other : initial);
        if (queuedAtEntry) h.Settings.Skin = initial;
        using var session = new ConnectionSession();
        h.Set("_session", session);
        h.Set("_trainingSounds", new Dictionary<int, Microsoft.Xna.Framework.Audio.SoundEffectInstance>());
        h.Call("SaveTrainingPreferences");
        Assert.Equal(initial, h.Switcher.Active.Renderer.Id);
        h.Switcher.Cycle(); h.Commit();
        Assert.NotEqual(initial, h.Settings.Skin);
        h.Call("ReleaseTraining");
        Assert.Equal(initial, h.Settings.Skin);
        Assert.Equal(initial, h.Switcher.Active.Renderer.Id);
        Assert.Equal(initial, HudSettings.Load(h.HudPath, true).Skin);
        h.Call("ReleaseTraining"); // idempotent, no extra cycle
        Assert.Equal(initial, h.Switcher.Active.Definition.Id);
    }

    [Theory]
    [InlineData(InputLayout.Port)]
    [InlineData(InputLayout.Native)]
    public void Selection_and_dispatch_allocate_zero_bytes_per_frame(InputLayout layout)
    {
        using var h = new Harness();
        KeyBindingStore.Instance.Map.SetLayout(layout);
        var frame = new HudDrawFrame(new Viewport(0, 0, 800, 480), null, false);
        var model = HudModelAdapter.Create(null, CameraMode.Command);
        // Bind the actual Game1 selector once; reflection stays outside the
        // measured loop, and a future allocation in ActiveHud is observable.
        var select = typeof(Game1).GetProperty("ActiveHud", Hidden)!.GetMethod!
            .CreateDelegate<Func<Game1, IHudSkinRenderer>>();
        var input = typeof(Game1).GetMethod("UpdateHudSkinInput", Hidden)!
            .CreateDelegate<Action<Game1, KeyboardState, bool>>();
        void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                // Exercise both steady selection and allocation-free preloaded
                // switching. Disk persistence/toast are event-time, outside Draw.
                input(h.Game, (i & 3) == 0 ? Cycle : default, true);
                h.Switcher.ApplyPending();
                var active = select(h.Game);
                active.Draw(model, frame);
                active.DrawCommand(model);
                active.DrawConsole(model);
            }
        }
        Frames(10_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Frames(100_000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P988 {layout} input/selection/dispatch: {bytes} B / 100000 frames = {bytes / 100000d} B/frame");
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Legacy_settings_assignments_queue_the_bound_renderer_without_per_frame_polling()
    {
        using var h = new Harness();
        h.Settings.Style = HudStyle.Modern;
        Assert.Equal(HudStyle.Classic, h.Settings.Style);
        Assert.Equal(HudSkinIds.Classic, h.Switcher.Active.Renderer.Id);
        h.Commit();
        Assert.Equal(HudStyle.Modern, h.Settings.Style);
        Assert.Equal(HudSkinIds.Modern, h.Switcher.Active.Renderer.Id);
        h.Settings.Skin = "classic";
        h.Commit();
        Assert.Equal(HudSkinIds.Classic, h.Switcher.Active.Renderer.Id);
        h.Settings.Skin = "unavailable";
        h.Commit();
        Assert.Equal(HudSkinIds.Classic, h.Settings.Skin);
        Assert.Equal(HudSkinIds.Classic, h.Switcher.Active.Renderer.Id);
        h.Switcher.Dispose();
        h.Settings.Style = HudStyle.Modern; // ordinary unbound settings after teardown
        Assert.Equal(HudStyle.Modern, h.Settings.Style);
    }

    [Fact]
    public void Invalid_skin_keeps_active_and_pending_resources_and_disposal_is_once()
    {
        var settings = new HudSettings { Skin = HudSkinIds.Classic };
        var classic = new Stub(HudSkinIds.Classic); var modern = new Stub(HudSkinIds.Modern);
        var switcher = new HudSkinSwitcher(settings, classic, modern);
        Assert.False(switcher.Request("jet"));
        Assert.False(switcher.ApplyPending());
        Assert.True(switcher.Request(" MODERN "));
        Assert.False(switcher.Request("missing"));
        Assert.True(switcher.ApplyPending());
        Assert.Same(modern, switcher.Active.Renderer);
        switcher.Dispose(); switcher.Dispose();
        Assert.Equal(1, classic.Disposals); Assert.Equal(1, modern.Disposals);
    }

    [Fact]
    public void Classic_adapter_preserves_exact_pass_order_and_borrows_frame_preview()
    {
        var calls = new List<string>();
        var drawing = new RecordingHud(calls);
        var model = HudModelAdapter.Create(null, CameraMode.Command);
        var resources = new HudSkinResources(_ => calls.Add("radar"), (_, modern) => calls.Add("tags:" + modern),
            _ => calls.Add("pod/death"), (_, classic) => calls.Add("command:" + classic),
            _ => calls.Add("chat"), _ => calls.Add("cargo"), _ => calls.Add("parts"));
        using var renderer = new HudSkinRenderer(drawing, resources);
        renderer.Draw(model, new(new(0, 0, 800, 480), null, true));
        renderer.DrawCommand(model);
        calls.Add("touch/starmap");
        renderer.DrawConsole(model);
        Assert.Equal(new[] { "radar", "tags:False", "hud", "pod/death", "command:True", "touch/starmap", "chat", "cargo", "parts" }, calls);
        Assert.True(drawing.SelectionBracket);
        Assert.Null(drawing.TargetModelPreview);
    }

    [Fact]
    public void Modern_adapter_keeps_command_geometry_and_does_not_duplicate_console()
    {
        var calls = new List<string>();
        var modern = (ModernHudRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ModernHudRenderer));
        GC.SuppressFinalize(modern);
        var services = new HudSkinResources(_ => calls.Add("radar"), (_, _) => calls.Add("tags"),
            _ => calls.Add("pod/death"), (_, classic) => calls.Add("command:" + classic),
            _ => calls.Add("chat"), _ => calls.Add("cargo"), _ => calls.Add("parts"));
        var adapter = new HudSkinRenderer(modern, services);
        var model = HudModelAdapter.Create(null, CameraMode.Command);
        adapter.DrawCommand(model);
        adapter.DrawConsole(model);
        Assert.Equal(new[] { "command:False" }, calls);
        Assert.Equal(HudSkinIds.Modern, adapter.Id);
        // The fake has no SDF resources; no real renderer lifetime is started.
    }

    private sealed class RecordingHud(List<string> calls) : HudRenderer(null!, null!, null!, null!)
    {
        public override void Draw(HudModel model, Viewport viewport) => calls.Add("hud");
    }

    private sealed class Stub(string id) : IHudSkinRenderer
    {
        public string Id => id;
        public IReadOnlySet<string>? Coverage => null;
        public int Disposals;
        public void Draw(HudModel model, in HudDrawFrame frame) { }
        public void DrawCommand(HudModel model) { }
        public void DrawConsole(HudModel model) { }
        public void SetRadarIcons(RadarIconTextures? icons) { }
        public Rectangle DrawOverlayText(string text, Vector2 position, Color color) => default;
        public void Dispose() => Disposals++;
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _savedMap = KeyBindingStore.Instance.Map.ToJson();
        private readonly InputLayout _savedLayout = KeyBindingStore.Instance.Map.Layout;
        private readonly Func<string, bool>? _savedFilter = KeyBindingStore.Instance.Map.ActionFilter;
        private readonly string? _oldHud = Environment.GetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS");
        private readonly string? _oldGraphics = Environment.GetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS");
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "p988-" + Guid.NewGuid().ToString("N"));
        public string HudPath => Path.Combine(_dir, "hud.json");
        public Game1 Game { get; }
        public HudSettings Settings { get; }
        public HudSkinSwitcher Switcher { get; }

        public Harness(string skin = HudSkinIds.Classic)
        {
            Game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(Game);
            Settings = new() { Skin = skin, Level = HudLevel.Combat, SelectionBracket = false, RadarDeclutter = false };
            Switcher = new(Settings, new Stub(HudSkinIds.Classic), new Stub(HudSkinIds.Modern));
            Set("_hudSettings", Settings); Set("_hudSkins", Switcher);
            Set("_pilotProfile", new PilotInfoLayout.ProfileEditor());
            Set("_flightGc", new FlightGcLatency(false));
            Set("_upscaling", new UpscalingSettings()); Set("_resolutionController", new DynamicResolutionController());
            var state = typeof(Game1).GetField("_state", Hidden)!;
            state.SetValue(Game, Enum.Parse(state.FieldType, "Flying"));
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", HudPath);
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", Path.Combine(_dir, "graphics.json"));
            KeyBindingStore.Instance.Map.ResetToDefaults(); KeyBindingStore.Instance.Map.ActionFilter = null;
        }

        public void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(Game, value);
        public T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(Game)!;
        public object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(Game, args);
        public void Input(KeyboardState keys, bool focused = true) => Call("UpdateHudSkinInput", keys, focused);
        public void Commit() => Call("CommitHudSkinChange");
        public void Dispose()
        {
            Switcher.Dispose();
            KeyBindingStore.Instance.Map.SetLayout(_savedLayout);
            KeyBindingStore.Instance.Map.ResetToDefaults(); KeyBindingStore.Instance.Map.ApplyJson(_savedMap);
            KeyBindingStore.Instance.Map.ActionFilter = _savedFilter;
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", _oldHud);
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", _oldGraphics);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
    }
}
