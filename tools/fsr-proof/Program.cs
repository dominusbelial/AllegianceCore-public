using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
string? output = args.Length > 1 ? args[1] : null;
using var game = new FsrProof(root, output);
game.Run();
return 0;

/// <summary>Real GL pipeline test, independent of artwork/server. Can run under
/// SDL_VIDEODRIVER=offscreen on Mesa/EGL or in a normal DesktopGL window.</summary>
sealed class FsrProof : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly string _root;
    private readonly string? _output;
    private ShaderAssetCache _cache = null!;
    private FsrPostProcessor _fsr = null!;
    private BloomPostProcessor _bloom = null!;
    private GlFrameTimer? _timer;
    private SpriteBatch _batch = null!;
    private Texture2D _white = null!;
    private int _frame, _gpuSamples;
    private double _gpuMs;
    private readonly Color[] _pixels = new Color[321 * 181];
    public FsrProof(string root, string? output)
    {
        _root = root; _output = output;
        _graphics = new(this) { PreferredBackBufferWidth = 321, PreferredBackBufferHeight = 181,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
        IsFixedTimeStep = false;
    }
    protected override void LoadContent()
    {
        _cache = new(bytes => new Effect(GraphicsDevice, bytes));
        foreach (string stem in new[] { "FsrEasu", "FsrRcas", "Bloom" })
        {
            string relative = $"Content/Shaders/{stem}.ogl.mgfxo";
            string path = Path.Combine(_root, "src/Allegiance.ClientCore.MonoGame", relative);
            if (_cache.LoadFrom(relative, () => File.OpenRead(path)) is null) throw new Exception($"Shader failed to load: {stem}");
        }
        _fsr = new(GraphicsDevice, _cache);
        if (!_fsr.Ready) throw new Exception("FSR effects incomplete");
        _bloom = new(GraphicsDevice, _cache.Load("Content/Shaders/Bloom.ogl.mgfxo")!);
        _batch = new(GraphicsDevice); _white = new(GraphicsDevice, 1, 1); _white.SetData(new[] { Color.White });
        _timer = GlFrameTimer.TryCreate();
        Console.WriteLine($"GL adapter: {GraphicsAdapter.DefaultAdapter.Description}; timer={_timer is not null}");
    }
    protected override void Draw(GameTime time)
    {
        _frame++;
        _timer?.Poll((_, _, ms) => { _gpuSamples++; _gpuMs += ms; });
        var preset = (FsrPreset)((_frame / 8) % 4);
        float scale = UpscalingSettings.Scale(preset);
        _fsr.SetSize(321, 181, scale);
        var scene = _fsr.Scene!;
        _timer?.Start(_frame, false);
        GraphicsDevice.SetRenderTarget(scene);
        GraphicsDevice.Clear(Color.Black);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
        // Four quadrants detect flipped/rotated output, flat extremes detect reciprocal NaNs.
        int hw = scene.Width / 2, hh = scene.Height / 2;
        _batch.Draw(_white, new Rectangle(0, 0, hw, hh), Color.Red);
        _batch.Draw(_white, new Rectangle(hw, 0, scene.Width-hw, hh), Color.Green);
        _batch.Draw(_white, new Rectangle(0, hh, hw, scene.Height-hh), Color.Blue);
        _batch.Draw(_white, new Rectangle(hw, hh, scene.Width-hw, scene.Height-hh), Color.White);
        for (int x = 8; x < hw - 8; x++) _batch.Draw(_white, new Rectangle(x, x / 2 + 5, 1, 2), Color.Black);
        _batch.End();
        _timer?.Stop();
        _timer?.Start(_frame, true);
        Texture2D source = scene;
        bool bloom = _frame % 2 == 0;
        if (bloom)
        {
            _bloom.SetSize(scene.Width, scene.Height);
            GraphicsDevice.SetRenderTarget(_fsr.Bloomed);
            _bloom.Render(scene); source = _fsr.Bloomed!;
        }
        _fsr.Render(source, null, _frame % 3 == 0 ? 0 : 1);
        _timer?.Stop();
        // Native HUD test: alternating one-pixel columns must survive exactly.
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
        for (int x = 0; x < 321; x++) _batch.Draw(_white, new Rectangle(x, 173, 1, 8), x % 2 == 0 ? Color.White : Color.Black);
        _batch.End();
        GraphicsDevice.GetBackBufferData(_pixels);
        if (_pixels.Length != GraphicsDevice.PresentationParameters.BackBufferWidth * GraphicsDevice.PresentationParameters.BackBufferHeight)
            throw new Exception("Wrong output dimensions");
        Check(20, 12, Color.Red); Check(300, 12, Color.Green); Check(20, 150, Color.Blue); Check(300, 150, Color.White);
        for (int x = 0; x < 321; x++) Check(x, 177, x % 2 == 0 ? Color.White : Color.Black);
        if (_output is not null && _frame % 8 == 7)
        {
            Directory.CreateDirectory(_output);
            using var texture = new Texture2D(GraphicsDevice, 321, 181); texture.SetData(_pixels);
            using var file = File.Create(Path.Combine(_output, $"{preset}.png")); texture.SaveAsPng(file, 321, 181);
        }
        if (_frame >= 40)
        {
            if (_timer is not null && (_gpuSamples == 0 || _gpuMs <= 0)) throw new Exception("Timer queries produced no positive results");
            Console.WriteLine($"FSR_SELF_TEST_PASS: 40 scene->EASU->RCAS->backbuffer frames; 321x181; all presets, resize, bloom, sharpness endpoints, orientation, black/white, native HUD; GPU samples={_gpuSamples}");
            Exit();
        }
        base.Draw(time);
    }
    private void Check(int x, int y, Color expected)
    {
        Color actual = _pixels[y * 321 + x];
        if (Math.Abs(actual.R-expected.R)>2 || Math.Abs(actual.G-expected.G)>2 || Math.Abs(actual.B-expected.B)>2)
            throw new Exception($"Pixel ({x},{y}) actual={actual} expected={expected}; frame {_frame}");
    }
    protected override void UnloadContent()
    {
        _timer?.Dispose(); _batch.Dispose(); _white.Dispose(); _fsr.Dispose(); _bloom.Dispose(); _cache.Dispose();
        base.UnloadContent();
    }
}
