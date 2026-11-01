using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

if (args.Length != 3) throw new ArgumentException("usage: p910-training-proof <content> <output> <Classic|Modern>");
var style = Enum.Parse<HudStyle>(args[2]);
using var game = new Game1(new Game1.LaunchOptions { Intro = true, PlayerName = "Training Pilot", WindowWidth = 800, WindowHeight = 480 });
game.Content.RootDirectory = Path.GetFullPath(args[0]);
game.Components.Add(new TrainingProof(game, Path.GetFullPath(args[1]), style));
game.Run();
game.Dispose(); // real window unload must release a still-active lesson
if (Allegiance.ClientCore.Training.TrainingController.IsTraining || TrainingOverlay.Current != 0)
    throw new InvalidOperationException("Window close left training active.");
var hud = (HudSettings)typeof(Game1).GetField("_hudSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
if (hud.Style != style) throw new InvalidOperationException("Window close failed to restore HUD style.");
Console.WriteLine("P910_WINDOW_ROUTES_PASS: explicit quit, transport loss, window unload");

sealed class TrainingProof(Game1 game, string output, HudStyle style) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step;
    private double _next;
    private T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private void Set(string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private object? Call(string method, params object?[] args) => typeof(Game1).GetMethods(Hidden)
        .Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(game, args);
    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
    }
    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 60) throw new TimeoutException("Training window proof timed out.");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        if (_step == 0)
        {
            Get<HudSettings>("_hudSettings").Style = style;
            Call("BeginTrainingMission", 2); _step++; _next = _watch.Elapsed.TotalSeconds + 4; return;
        }
        if (_step == 1)
        {
            if (Get<TrainingSession?>("_trainingSession") is not { Started: true }) return;
            Call("DismissBriefing");
            // Real shared tip receiver: only the second lesson tip occupies slot zero.
            Call("PushTip", "Previous training tip", true);
            Call("PushTip", "Training: use the radar to find the Commander", true);
            Shot($"{style.ToString().ToLowerInvariant()}-lesson");
            _step++; _next = _watch.Elapsed.TotalSeconds + 1; return;
        }
        if (_step == 2)
        {
            Call("RequestTrainingQuit"); Shot($"{style.ToString().ToLowerInvariant()}-quit");
            _step++; _next = _watch.Elapsed.TotalSeconds + 1; return;
        }
        if (_step == 3)
        {
            Call("ConfirmTrainingQuit", true);
            if (Get<TrainingSession?>("_trainingSession") is not null || TrainingOverlay.Current != 0
                || Get<HudSettings>("_hudSettings").Style != style) throw new InvalidOperationException("Training preferences leaked.");
            Shot($"{style.ToString().ToLowerInvariant()}-chooser");
            _step++; _next = _watch.Elapsed.TotalSeconds + 1; return;
        }
        if (_step == 4)
        {
            if (Get<bool>("_autoShotScheduled")) return;
            Call("BeginTrainingMission", 2); _step++; return;
        }
        if (_step == 5)
        {
            if (Get<TrainingSession?>("_trainingSession") is not { Started: true }) return;
            Get<Allegiance.SinglePlayer.TrainingHost>("_embeddedHost").Dispose();
            _step++; return;
        }
        if (_step == 6)
        {
            if (Get<TrainingSession?>("_trainingSession") is not null) return;
            if (Allegiance.ClientCore.Training.TrainingController.IsTraining || TrainingOverlay.Current != 0)
                throw new InvalidOperationException("Transport loss left training active.");
            Call("BeginTrainingMission", 2); _step++; return;
        }
        if (Get<TrainingSession?>("_trainingSession") is { Started: true })
        {
            TrainingOverlay.Current = 99;
            Get<HudSettings>("_hudSettings").Style = style == HudStyle.Classic ? HudStyle.Modern : HudStyle.Classic;
            game.Exit();
        }
    }
}
