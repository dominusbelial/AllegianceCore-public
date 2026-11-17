using System.Text;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Browser lifecycle for the shared Game1 intro/options partials. No connection/offline session is created.</summary>
public sealed partial class Game1 : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly IAssetSource _assets;
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _whitePixel = null!;
    private SpriteFont _hudSmall = null!, _hudBig = null!;
    private readonly IdentityScreenModel _identityScreen = new() { Status = "Browser preview - online play is not available yet." };
    private readonly StringBuilder _pilotBuf = new();
    private static readonly string[] IntroMainLabels = IntroScreenModel.MainLabels;
    private int _introIndex;
    private bool _introEditingPilot = true, _iUp, _iDown, _iEnter, _iTab;
    private enum IntroView { Main, Settings, SinglePlayer, Unavailable }
    private IntroView _introView;
    private readonly OptionsMenuModel _optionsMenu = new();
    private readonly SinglePlayerChooserModel _singlePlayerChooser = new();
    private readonly MouseFlightController _mouseFlight = new();
    private bool _mouseFlightEnabled;
    private string? _playerPrefsPath = null;
    private readonly VisualOptions _visuals;
    private MenuAudio? _menuAudio;
    private Audio.IMenuSound? OptionsSound => _menuAudio;
    private Audio.IMenuMusic? OptionsMusic => null;
    private bool? OptionsHdrSceneActive => false;
    private bool _graphicsSettingsVisible;
    private bool _chatFilterChatsToAll;
    private readonly Audio.ForceFeedbackController ForceFeedback = new(null);
    private KeyboardState _previous;
    private bool _keyboardFocus = true;
    private string _unavailable = "";
    public bool Started { get; set; }
    public string Screen => _introView.ToString();
    public string OptionsPage => _optionsMenu.Current.ToString();
    public bool InvertMouseY => _mouseFlight.InvertPitch;
    public bool MenuSoundEnabled => _menuAudio is not null;
    private readonly HudSettings _hudSettings = new();
    public string HudStyleName => _hudSettings.Style.ToString();
    public void SelectHudStyle(HudStyle style) => _hudSettings.Style = style;
    public long ManagedHeapBytes => GC.GetTotalMemory(false);
    public Action? DownloadScreenshot { get; set; }

    public Game1(IAssetSource assets, HudStyle style, int width, int height)
    {
        _assets = assets;
        _hudSettings.Style = style;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = width, PreferredBackBufferHeight = height,
            // Desktop XNB SpriteFonts contain non-power-of-two DXT3 atlases.
            // HiDef permits those dimensions; every render surface/effect here is still LDR.
            GraphicsProfile = GraphicsProfile.HiDef, PreferredBackBufferFormat = SurfaceFormat.Color,
        };
        IsFixedTimeStep = false;
        IsMouseVisible = true;
        Content = new ResidentContentManager(Services, assets) { RootDirectory = "Content" };
        var prefs = IntroPreferences.Load();
        _pilotBuf.Append(prefs.Pilot);
        MouseOptions.Apply(_mouseFlight, prefs);
        _visuals = VisualOptions.Resolve(prefs.Visuals, null, out _);
        _chatFilterChatsToAll = prefs.FilterChatsToAll;
        Console.WriteLine("[browser] LDR Color backbuffer. LinearDepth, SoftParticle, Bloom, Volumetric, DisplacedSphere, NativePbr, FsrEasu, FsrRcas and Tonemap disabled: desktop .mgfxo bytecode is not KNI/WebGL content; using built-in SpriteBatch.");
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _whitePixel = new Texture2D(GraphicsDevice, 1, 1);
        _whitePixel.SetData(new[] { Color.White });
        // Exact desktop font bakes, read only from the staged in-memory content source.
        _hudSmall = Content.Load<SpriteFont>("HUD");
        _hudBig = Content.Load<SpriteFont>("HUDLarge");
        _hudSmall.DefaultCharacter = '?'; _hudBig.DefaultCharacter = '?';
        _ = Content.Load<SpriteFont>("HUDModern");
        foreach (var spec in Enum.GetValues<NativeFont>().Select(NativeFontCatalog.Spec))
        {
            _ = Content.Load<SpriteFont>(spec.Asset1x);
            _ = Content.Load<SpriteFont>(spec.Asset2x);
        }
        _ = Allegiance.Core.IgcReader.Parse(AssetSources.ReadAllBytes(_assets, "PCore001.igc"));
    }

    protected override void Update(GameTime gameTime)
    {
        var keys = _keyboardFocus ? Keyboard.GetState() : default;
        bool up = keys.IsKeyDown(Keys.Up), down = keys.IsKeyDown(Keys.Down);
        bool enter = keys.IsKeyDown(Keys.Enter), tab = keys.IsKeyDown(Keys.Tab);
        bool Press(Keys key) => keys.IsKeyDown(key) && !_previous.IsKeyDown(key);
        if (_introView == IntroView.Main) UpdateIntroMainKeys(up, down, enter, tab);
        else if (_introView == IntroView.Settings) UpdateOptionsMenu(keys);
        else if (_introView == IntroView.SinglePlayer)
        {
            if (Press(Keys.Up)) _singlePlayerChooser.Move(-1);
            if (Press(Keys.Down)) _singlePlayerChooser.Move(1);
            if (Press(Keys.Enter)) ShowUnavailable(_singlePlayerChooser.Activate() ? "Training" : "Skirmish");
            if (Press(Keys.Escape)) _introView = IntroView.Main;
        }
        else if (Press(Keys.Escape)) _introView = IntroView.Main;
        if (_graphicsSettingsVisible) { _graphicsSettingsVisible = false; ShowUnavailable("Graphics calibration and flight effects"); }
        if (Press(Keys.F12)) DownloadScreenshot?.Invoke();
        _iUp = up; _iDown = down; _iEnter = enter; _iTab = tab;
        _previous = keys;
        base.Update(gameTime);
    }

    public void InputText(string text)
    {
        if (!_keyboardFocus || _introView != IntroView.Main || !_introEditingPilot) return;
        if (text == "Backspace") { if (_pilotBuf.Length > 0) _pilotBuf.Length--; }
        else if (text.Length == 1 && IntroScreenModel.TypesPilotName(text[0], _introEditingPilot) && _pilotBuf.Length < 24) _pilotBuf.Append(text);
        var prefs = IntroPreferences.Load(); prefs.Pilot = _pilotBuf.ToString(); prefs.Save();
    }

    public void SetKeyboardFocus(bool focused)
    {
        if (_keyboardFocus == focused) return;
        _keyboardFocus = focused;
        if (!Started) return;
        // Returning from an HTML control must not turn its held Enter/Tab into
        // a new native command. Keep rendering while that control owns input.
        var keys = Keyboard.GetState();
        _previous = _iPrivacyKeys = keys;
        _iUp = keys.IsKeyDown(Keys.Up); _iDown = keys.IsKeyDown(Keys.Down);
        _iEnter = keys.IsKeyDown(Keys.Enter); _iTab = keys.IsKeyDown(Keys.Tab);
    }

    private void ActivateIntroMain()
    {
        _menuAudio?.StartSoundId(Allegiance.Core.Audio.NativeSoundCatalog.IdOf("mouseclickSound"));
        var command = IntroScreenModel.Activate(_introIndex);
        if (command == IntroCommand.Options)
        {
            _introView = IntroView.Settings;
            _optionsMenu.Reset();
            // Opening Enter belongs to the intro, not to the new page's first command.
            _iPrivacyKeys = Keyboard.GetState();
            _optionsPrefsLoaded = false;
            EnsureOptionsPrefs();
        }
        else if (command == IntroCommand.SinglePlayer) _introView = IntroView.SinglePlayer;
        else ShowUnavailable(IntroMainLabels[_introIndex]);
    }
    private void ShowUnavailable(string feature) { _unavailable = feature + " is not available in the browser."; _introView = IntroView.Unavailable; }
    private void ToggleKeyBindings() => ShowUnavailable("Controls for flight");
    partial void HandlePlatformOptionsCommand(OptionsMenuModel.OptionsCommand command, ref bool handled)
    {
        if (command is OptionsMenuModel.OptionsCommand.GraphicsSettings or OptionsMenuModel.OptionsCommand.MapKeysAndControls
            or OptionsMenuModel.OptionsCommand.ToggleMusic or OptionsMenuModel.OptionsCommand.MusicVolumeUp
            or OptionsMenuModel.OptionsCommand.MusicVolumeDown or OptionsMenuModel.OptionsCommand.ToggleMuteChatSender)
        {
            ShowUnavailable(command switch
            {
                OptionsMenuModel.OptionsCommand.GraphicsSettings => "Flight graphics",
                OptionsMenuModel.OptionsCommand.MapKeysAndControls => "Flight controls",
                OptionsMenuModel.OptionsCommand.ToggleMuteChatSender => "Online chat",
                _ => "Background music",
            }); handled = true;
        }
    }
    public bool EnableMenuAudio()
    {
        try { _menuAudio ??= new MenuAudio(Content.Load<Microsoft.Xna.Framework.Audio.SoundEffect>("Sounds/mousedown"), IntroPreferences.Load()); _menuAudio.StartSoundId(Allegiance.Core.Audio.NativeSoundCatalog.IdOf("mouseclickSound")); }
        catch (Exception error) { Console.WriteLine("[browser] Menu audio unavailable: " + error.Message); }
        return MenuSoundEnabled;
    }
    private void SetInvertMouseY(bool inverted)
    {
        _mouseFlight.InvertPitch = inverted;
        EnsureOptionsPrefs();
        MouseOptions.Capture(_mouseFlight, _optionsPrefs);
        _optionsPrefs.Save();
    }
    private void StepMouseSensitivity(int direction)
    {
        _mouseFlight.MouseSensitivity = MouseOptions.Step(_mouseFlight.MouseSensitivity, direction);
        EnsureOptionsPrefs();
        MouseOptions.Capture(_mouseFlight, _optionsPrefs);
        _optionsPrefs.Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menuAudio?.Dispose();
        base.Dispose(disposing);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(5, 12, 20));
        var vp = GraphicsDevice.Viewport;
        var title = new Color(HudColor.TealAccent.R, HudColor.TealAccent.G, HudColor.TealAccent.B);
        var dim = new Color(.48f, .64f, .62f);
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        try
        {
            if (_introView == IntroView.Main) DrawIntroMain(vp.Width, vp.Height, title, dim, new Color(.88f, .96f, .95f));
            else if (_introView == IntroView.Settings) DrawPrivacySettings(vp.Width, vp.Height, title, dim);
            else if (_introView == IntroView.SinglePlayer) DrawIntroChooser(vp.Width, vp.Height, title, dim, new Color(.88f, .96f, .95f));
            else
            {
                DrawIntroTitle(vp.Width, "Browser preview", "", title, dim);
                string line = _unavailable;
                var size = _hudSmall.MeasureString(line);
                _spriteBatch.DrawString(_hudSmall, line, new Vector2((vp.Width - size.X) / 2, vp.Height * .42f), title);
                _spriteBatch.DrawString(_hudSmall, "ESC back", new Vector2(40, vp.Height - 40), dim);
            }
        }
        finally { _spriteBatch.End(); }
        base.Draw(gameTime);
    }
}
