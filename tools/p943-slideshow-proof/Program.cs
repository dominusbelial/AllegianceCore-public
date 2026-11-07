using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Microsoft.Xna.Framework;

// P943 — render every training slideshow's slides through the shipping Game1 intro
// screen and write one real-backbuffer PNG per slide, so the artwork can be reviewed
// (the pre-change screen was black: the port drew captions only).
//   usage: p943-slideshow-proof <content-dir> <output-dir>
// Run it with an offscreen SDL window:
//   SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null
if (args is not [var content, var output])
    throw new ArgumentException("usage: p943-slideshow-proof <content> <output>");
Directory.CreateDirectory(output);
var artwork = new DirectoryAssetSource(ExternalPaths.Require(ExternalResourceKind.Artwork));
using var game = new Game1(new Game1.LaunchOptions
    { Intro = true, PlayerName = "Slide Proof", WindowWidth = 800, WindowHeight = 480 });
game.Content.RootDirectory = Path.GetFullPath(content);
var proof = new SlideshowProof(game, Path.GetFullPath(output), artwork);
game.Components.Add(proof);
game.Run();
if (!proof.Completed) throw new InvalidOperationException("slideshow proof exited before every slide was captured.");
Console.WriteLine($"P943_SLIDESHOW_PASS shots={proof.ShotCount}");

/// <summary>Walks the native namespace table (WinTrek.cpp:2473-2522), opens each slideshow
/// on the shipping intro screen and photographs every slide with the game's own
/// autoshot (the real backbuffer). The window is the game's 800×480 default: SDL's offscreen
/// driver never resizes its surface, so a larger request is clipped to 800×480.</summary>
internal sealed class SlideshowProof(Game1 game, string output, IAssetSource artwork) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<(int Mission, bool Post, string Namespace)> _targets = [.. SlideshowNamespaces.All()];
    private readonly Type _introView = typeof(Game1).GetNestedType("IntroView", Hidden)!;
    private int _targetIndex;
    private int _slideIndex;
    private int _settleFrames;
    private SlideshowModel? _model;
    private bool _shotQueued;

    public bool Completed { get; private set; }
    public int ShotCount { get; private set; }

    private object? Get(string name) => typeof(Game1).GetField(name, Hidden)!.GetValue(game);
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 240)
            throw new TimeoutException($"slideshow proof timed out at {_targets[_targetIndex].Namespace} slide {_slideIndex + 1}");

        if (_targetIndex >= _targets.Count) { Completed = true; game.Exit(); return; }
        if (Get("_state")?.ToString() != "Intro") return;   // the splash runs first

        if (_model is null)
        {
            var (mission, post, ns) = _targets[_targetIndex];
            string name = TrainingScreenModel.NameOf(mission);
            _model = post ? TrainingSlides.PostMission(mission, name, artwork)
                          : TrainingSlides.PreMission(mission, name, artwork);
            _model.Start();
            Set("_trainingSlideshow", _model);
            Set("_trainingSlideshowIsPost", post);
            Set("_introView", Enum.Parse(_introView, "TrainingSlideshow"));
            _slideIndex = 0;
            _settleFrames = 0;
            Console.WriteLine($"[p943] {ns}: {_model.Count} slides");
            return;
        }

        if (_shotQueued)
        {
            if ((bool)Get("_autoShotScheduled")!) return;   // not flushed yet
            _shotQueued = false;
            ShotCount++;
            if (_slideIndex + 1 < _model.Count) { _slideIndex++; _model.OnKey(); }
            else { _model = null; _targetIndex++; }
            _settleFrames = 0;
            return;
        }

        if (++_settleFrames < 3) return;
        Set("_autoshotDir", output);
        Set("_autoShotName", $"p943-{_targets[_targetIndex].Namespace}-{_slideIndex + 1:00}");
        Set("_autoShotScheduled", true);
        _shotQueued = true;
    }
}
