using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Microsoft.Xna.Framework;
using V = System.Numerics.Vector3;

if (args is not [var content, var output, var styleName])
    throw new ArgumentException("usage: p934-training-proof <content> <output> <Classic|Modern>");
var style = Enum.Parse<HudStyle>(styleName);
Directory.CreateDirectory(output);
using var game = new Game1(new Game1.LaunchOptions
    { Intro = true, PlayerName = "Training Pilot", WindowWidth = 800, WindowHeight = 480 });
game.Content.RootDirectory = Path.GetFullPath(content);
var proof = new VisibilityProof(game, Path.GetFullPath(output), style);
game.Components.Add(proof);
game.Run();
if (!proof.Completed) throw new InvalidOperationException("Visibility proof exited before both captures.");
Console.WriteLine($"P934_WINDOW_PASS {style}: combined enemy eye -> real wire Detected -> shipping HUD; loss decays both.");

sealed class VisibilityProof(Game1 game, string output, HudStyle style) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _stage;
    private double? _stableSince;
    public bool Completed { get; private set; }
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private void Call(string name, params object[] args) => typeof(Game1).GetMethods(Hidden)
        .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(game, args);
    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", $"p934-{style.ToString().ToLowerInvariant()}-{name}");
        Set("_autoShotScheduled", true);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("Visibility window timed out.");
        Get<HudSettings>("_hudSettings").Style = style;
        if (_stage == 0) { Call("BeginTrainingMission", 2); _stage++; return; }
        if (Get<TrainingSession?>("_trainingSession") is not { Started: true } session ||
            Get<TrainingHost?>("_embeddedHost") is not { } host) return;
        session.Site.SetDisplayMode(TrainingMissionIds.CameraCockpit);
        var world = Get<ConnectionSession>("_session").World;
        if (_stage == 1)
        {
            Call("DismissBriefing");
            host.CreateDrone("Enemy scanner", 9343, 315, 1, Ship.PilotTypePlayer);
            host.PlaceDrone(9343, host.StartSectorId, session.Site.PlayerShip.Position + new V(0, 50, 0),
                V.UnitX, V.UnitZ, null, 0);
            _stage++; return;
        }
        if (_stage is 2 or 4)
        {
            bool expected = _stage == 2;
            bool eye = host.PlayerShip.GetCurrentEye(2);
            bool detected = world.Ships[(short)host.PlayerShipId].Detected;
            if (eye != expected || detected != expected) { _stableSince = null; return; }
            _stableSince ??= _watch.Elapsed.TotalSeconds;
            if (_watch.Elapsed.TotalSeconds < _stableSince + 1) return;
            if (host.PlayerShip.SeenBySide(99) != expected) throw new InvalidOperationException("Arbitrary-side latch disagrees.");
            Console.WriteLine($"P934_CAPTURE {style} eye={eye} arbitrarySide99={expected} wireDetected={detected}");
            Shot(expected ? "eyed" : "clear"); _stage++; _stableSince = null; return;
        }
        if (Get<bool>("_autoShotScheduled")) return;
        if (_stage == 3)
        {
            host.PlaceDrone(9343, host.StartSectorId, new V(50000, 50000, 50000), V.UnitX, V.UnitZ, null, 0);
            _stage++; return;
        }
        if (_stage == 5) { Completed = true; game.Exit(); }
    }
}
