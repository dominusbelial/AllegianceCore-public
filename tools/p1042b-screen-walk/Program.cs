// P1042b — real-window walk over the pre-game intro sub-views.
//
// Runs the SHIPPING Game1 intro (no replacement renderer) on a fresh profile, sets
// each intro sub-view through the production field, and photographs the real
// backbuffer with the game's own autoshot (SaveAsPng). The same binary is run twice
// (Classic / Modern profile) so the Classic-vs-Modern and before-vs-after pixel
// compares are made on identical inputs.
//
// Usage (from the repository root; a fresh profile is the caller's job — the lane
// points HOME at a scratch dir):
//   SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
//   ALLEGIANCE_NOVSYNC=1 dotnet tools/p1042b-screen-walk/bin/Debug/net10.0/p1042b-screen-walk.dll \
//       <output-dir> [tag]
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "docs/reports/JET_SCREENS_P1042B_assets");
string tag = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "capture";
int width = ArgInt("--width", 800), height = ArgInt("--height", 480);

int ArgInt(string flag, int fallback)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var value) ? value : fallback;
}
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var game = new Game1(new Game1.LaunchOptions
{
    Intro = true,
    PlayerName = "",
    WindowWidth = width,
    WindowHeight = height,
});
game.Content.RootDirectory = content;
game.Components.Add(new IntroViewWalk(game, output, tag));
game.Run();

/// <summary>Sets each intro sub-view and photographs it through the game's autoshot.</summary>
sealed class IntroViewWalk(Game1 game, string output, string tag) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] Views =
    [
        "FindServer", "DirectConnect", "SinglePlayer", "SkirmishSetup", "Training",
        "TrainingSuggest", "Credits", "Help", "Career", "CreateGame", "HostLan",
        "JoinLink", "Settings", "Benchmark", // Benchmark last: its native paragraph
        // contains characters the HUD font cannot measure (a pre-existing crash, see
        // the report), so the process ends after this capture.
    ];

    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _view;
    private bool _shotPending;
    private int _shots;
    private bool _ready;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private string State => Get<object>("_state").ToString()!;
    private string View => Get<object>("_introView").ToString()!;

    private void SetView(string name)
    {
        var type = typeof(Game1).GetField("_introView", Hidden)!.FieldType;
        Set("_introView", Enum.Parse(type, name));
    }

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", $"walk-{_shots++:00}-{name}-{tag}");
        Set("_autoShotScheduled", true);
        Console.WriteLine("P1042B_WALK_SCREEN " + Get<string>("_autoShotName"));
    }

    private bool ShotFlushed => !Get<bool>("_autoShotScheduled");

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("P1042b walk did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        var vp = game.GraphicsDevice?.Viewport;
        if (!_ready)
        {
            // Hold the splash until the proof enters the intro.
            Set("_splashStartSeconds", 1e9);
            Console.WriteLine($"P1042B_WALK surface {vp?.Width}x{vp?.Height}");
            Set("_splashStartSeconds", Get<double>("_nowSeconds"));
            _ready = true;
            _next = _watch.Elapsed.TotalSeconds + 0.8;
            return;
        }
        if (!_shotPending)
        {
            if (_view >= Views.Length) { game.Exit(); return; }
            if (State != "Intro") { _next = _watch.Elapsed.TotalSeconds + 0.3; return; }
            SetView(Views[_view]);
            Shot(Views[_view]);
            _shotPending = true;
            _next = _watch.Elapsed.TotalSeconds + 0.5;
            return;
        }
        if (!ShotFlushed) { _next = _watch.Elapsed.TotalSeconds + 0.2; return; }
        Console.WriteLine($"P1042B_WALK {Views[_view]} view={View}");
        _shotPending = false;
        _view++;
        _next = _watch.Elapsed.TotalSeconds + 0.4;
    }
}
