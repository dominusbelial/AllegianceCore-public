// P946 — real-window proof that the intro menu keeps its arrow keys while the
// pilot-name field has focus, and that the field's focus is visible.
//
// Runs the shipping Game1 intro (no replacement renderer, no seeded rows). Keys
// are pressed through Game1's proof-only intro input seam
// (Game1._introProofKeys → ReadProofKeyboard → UpdateIntro), so the production
// edge latches and the production main-menu handler run exactly as they do for
// a player. Typed characters go through the game's real Window.TextInput
// handler (OnTextInput). Screenshots are the real backbuffer via the game's own
// autoshot (SaveAsPng).
//
// Usage (from the repository root; a fresh profile is the caller's job):
//   SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
//   ALLEGIANCE_NOVSYNC=1 \
//   dotnet tools/p946-intro-keys-proof/bin/Debug/net10.0/p946-intro-keys-proof.dll \
//       <output-dir> [tag]
//
// The optional tag names the captures (e.g. "classic" / "modern"); the HUD
// style itself comes from ALLEGIANCE_HUD_SETTINGS, and the intro ignores it
// (one shared intro renderer), so the two runs should be pixel-identical.
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Allegiance.ClientCore.MonoGame;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "docs/reports/P946_assets");
string tag = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "capture";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

// Keys go through Game1's proof-only intro input seam (Game1._introProofKeys →
// ReadProofKeyboard → UpdateIntro), so the production edge latches and handler
// run exactly as for a player. No ALLEGIANCE_TRAIN_PROOF gate is involved: the
// training autoproof resets its own key array every frame.
// SDL's offscreen driver gives at most a 480-px-tall surface (P943): a taller
// request renders only its bottom 480 rows, so the proof asks for the surface
// it actually gets (800x480) — the resolution the brief names for this proof.
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = true,
    PlayerName = "",
    WindowWidth = 800,
    WindowHeight = 480,
});
game.Content.RootDirectory = content;
game.Components.Add(new IntroKeyProof(game, output, tag));
game.Run();

/// <summary>Drives the shipping intro through real key edges and photographs it.</summary>
sealed class IntroKeyProof(Game1 game, string output, string tag) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private int _shot;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private string State => Get<object>("_state").ToString()!;
    private string View => Get<object>("_introView").ToString()!;
    private int Index => Get<int>("_introIndex");
    private string Pilot => Get<StringBuilder>("_pilotBuf").ToString();
    private bool PilotEditing => Get<bool>("_introEditingPilot");

    /// <summary>Hold a key for the next frame; the latch sees a real down edge.</summary>
    private void Press(params Keys[] keys) => Set("_introProofKeys", keys);
    private void Release() => Set("_introProofKeys", Array.Empty<Keys>());

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", $"p946-{_shot++:00}-{name}-{tag}");
        Set("_autoShotScheduled", true);
        Console.WriteLine("P946_SCREEN " + Get<string>("_autoShotName"));
    }

    private bool ShotFlushed => !Get<bool>("_autoShotScheduled");

    private void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("P946 proof failed: " + message);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P946 intro proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        var gd = game.GraphicsDevice;
        var vp = gd?.Viewport;
        switch (_step++)
        {
            case 0:
                // Hold the splash until the proof decides to enter the intro.
                Set("_splashStartSeconds", 1e9);
                Console.WriteLine($"P946 surface {vp?.Width}x{vp?.Height}");
                break;
            case 1:
                Set("_splashStartSeconds", Get<double>("_nowSeconds"));
                break;
            case 2:
                Require(State == "Intro", $"state is {State}, expected Intro");
                Require(View == "Main", $"view is {View}, expected Main");
                Require(Index == 0, $"selection starts at {Index}");
                Require(Pilot.Length == 0, $"pilot buffer starts as '{Pilot}' (fresh profile expected)");
                Require(PilotEditing, "pilot-name field is not focused at boot");
                Console.WriteLine($"P946 main view: index={Index} pilot='{Pilot}' focus={PilotEditing} surface={vp?.Width}x{vp?.Height}");
                Shot("main-focus");
                break;
            case 3:
                Require(ShotFlushed, "first screenshot did not flush");
                Press(Keys.Down);
                break;
            case 4:
                // Release in its own tick: pressing again before a frame reads
                // the release would keep the key held and produce no new edge.
                Release();
                Require(Index == 1, $"Down #1 left the selection at {Index}, expected 1 (field focused={PilotEditing})");
                break;
            case 5:
                Press(Keys.Down);
                break;
            case 6:
                Release();
                Require(Index == 2, $"Down #2 left the selection at {Index}, expected 2");
                Require(PilotEditing, "Down stole the pilot-name focus");
                // Typing still reaches the field after navigating (the task's
                // "printable characters still go to the name field").
                foreach (var c in "Nova") Call("OnTextInput", null!, new TextInputEventArgs(c, Keys.None));
                Require(Pilot == "Nova", $"typed name is '{Pilot}', expected 'Nova'");
                Console.WriteLine($"P946 after Down x2: index={Index} pilot='{Pilot}' focus={PilotEditing}");
                Shot("down-twice");
                break;
            case 7:
                Require(ShotFlushed, "second screenshot did not flush");
                Press(Keys.Up);
                break;
            case 8:
                Release();
                Require(Index == 1, $"Up left the selection at {Index}, expected 1");
                break;
            case 9:
                Press(Keys.Tab);
                break;
            case 10:
                Release();
                Require(!PilotEditing, "Tab did not release the pilot-name field (anonymous profile)");
                Console.WriteLine($"P946 after Tab: index={Index} focus={PilotEditing} (field released)");
                Shot("tab-menu-focus");
                break;
            case 11:
                Require(ShotFlushed, "third screenshot did not flush");
                Press(Keys.Tab);
                break;
            case 12:
                Release();
                Require(PilotEditing, "Tab did not restore the pilot-name field");
                Console.WriteLine($"P946_PASS surface={vp?.Width}x{vp?.Height} shots={_shot} site={output}");
                game.Exit();
                break;
            default:
                game.Exit();
                break;
        }
        _next = _watch.Elapsed.TotalSeconds + 0.6;
    }
}
