using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Microsoft.Xna.Framework;

if (args is ["sim-cost", ..]) { SimCost.Run(args[1..]); return; }
if (args is not ["window", var content, var output, var styleName])
    throw new ArgumentException("usage: p918-training-proof window <content> <output> <Classic|Modern> | sim-cost <artwork> <output>");
var style = Enum.Parse<HudStyle>(styleName);
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("ALLEGIANCE_TRAINING_START", "2");
Environment.SetEnvironmentVariable("ALLEGIANCE_TRAIN_PROOF", "1");
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = true, PlayerName = "Training Pilot", WindowWidth = 800, WindowHeight = 480,
    EffectsRequested = Allegiance.ClientCore.State.EffectsQuality.Native,
});
game.Content.RootDirectory = Path.GetFullPath(content);
var observer = new BuoyObserver(game, Path.GetFullPath(output), style);
game.Components.Add(observer);
game.Run();
if (!observer.Completed) throw new InvalidOperationException("The training window exited without completing the buoy proof.");
Console.WriteLine($"P918_WINDOW_PASS {style}");

sealed class BuoyObserver(Game1 game, string output, HudStyle style) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _createdAt, _retiredAt;
    private bool _created, _visibleShot, _retired, _retiredShot, _styleSet;
    public bool Completed { get; private set; }
    private T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private void Set(string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private static void Check(bool test, string reason) { if (!test) throw new InvalidOperationException(reason); }
    private void Shot(string name)
    { Set("_autoshotDir", output); Set("_autoShotName", $"p918-{style.ToString().ToLowerInvariant()}-{name}"); Set("_autoShotScheduled", true); }
    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("Training buoy proof did not reach its window.");
        if (!_styleSet) { Get<HudSettings>("_hudSettings").Style = style; _styleSet = true; }
        if (Get<TrainingSession?>("_trainingSession") is not { Started: true } session
            || Get<TrainingHost?>("_embeddedHost") is not { } host) return;
        Check(session.Mission is Mission2, "Expected the shipping Basic Flight mission.");
        var site = session.Site;
        if (!_created)
        {
            // Use the ordinary camera facility so both captures share a
            // cockpit view even while the native introduction changes views.
            site.SetDisplayMode(TrainingMissionIds.CameraCockpit);
            // Real lesson, real facilities and renderer. A small authored action
            // sequence places visible/hidden/unheld controls in the same camera
            // view; it changes no lesson condition, clock, input or outcome.
            var own = site.PlayerShip;
            var forward = own.Forward;
            var right = System.Numerics.Vector3.Cross(forward, System.Numerics.Vector3.UnitZ);
            var center = own.Position + forward * 700 + System.Numerics.Vector3.UnitZ * 160;
            Console.WriteLine($"P918_VIEW own={own.Position} forward={forward} center={center}");
            new CreateWaypointAction(site, 7600, center + right * 250, host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true).Execute();
            new CreateWaypointAction(site, 7601, center - right * 250, host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, false).Execute();
            new CreateWaypointAction(site, 7602, center, host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true).Execute();
            site.CreateDrone("visible buoy owner", 7610, 310, 0, Ship.PilotTypePlayer);
            site.CreateDrone("hidden buoy owner", 7611, 310, 0, Ship.PilotTypePlayer);
            site.PlaceDrone(7610, host.StartSectorId, new(9000, 9000, 9000), System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, -1);
            site.PlaceDrone(7611, host.StartSectorId, new(9000, -9000, 9000), System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, -1);
            new SetCommandAction(site, new(ObjectType.Ship, 7610), TrainingMissionIds.CommandQueued, new(ObjectType.Buoy, 7600), TrainingMissionIds.CmdGoto).Execute();
            new SetCommandAction(site, new(ObjectType.Ship, 7611), TrainingMissionIds.CommandQueued, new(ObjectType.Buoy, 7601), TrainingMissionIds.CmdGoto).Execute();
            Check(host.Mission.GetBuoy(7600) is { Visible: true, Radius: 20, ConsumerCount: 1 }, "Visible buoy ownership/geometry mismatch.");
            Check(host.Mission.GetBuoy(7601) is { Visible: false, Radius: 2, ConsumerCount: 1 }, "Hidden buoy ownership/geometry mismatch.");
            Check(host.Mission.GetBuoy(7602) is { Visible: false, ConsumerCount: 0 }, "Unheld buoy must remain invisible.");
            _created = true; _createdAt = _watch.Elapsed.TotalSeconds;
        }
        var drawn = Get<List<Buoy.Visual>>("_drawCommandBuoys");
        if (!_visibleShot && _watch.Elapsed.TotalSeconds > _createdAt + 3)
        {
            Check(drawn.Any(b => b.Id == 7600), "Shipping scene pass did not draw the held visible buoy.");
            Check(drawn.All(b => b.Id is not (7601 or 7602)), "Shipping scene drew a hidden or unheld buoy.");
            // TextureFor is the renderer's real cache; require native artwork,
            // not a test overlay or generated substitute.
            Check(Get<ProjectileSpriteRenderer>("_projectileSpriteRenderer").TextureFor("buoy") is not null, "Missing native buoy decal.");
            Shot("visible"); _visibleShot = true;
        }
        if (_visibleShot && !_retired && !Get<bool>("_autoShotScheduled"))
        {
            new SetCommandAction(site, new(ObjectType.Ship, 7610), TrainingMissionIds.CommandQueued, null, TrainingMissionIds.CmdNone).Execute();
            Check(host.Mission.GetBuoy(7600) is null, "Clearing the last slot did not retire its buoy.");
            _retired = true; _retiredAt = _watch.Elapsed.TotalSeconds;
        }
        if (_retired && !_retiredShot && _watch.Elapsed.TotalSeconds > _retiredAt + 1)
        {
            Check(drawn.All(b => b.Id is not (7600 or 7601 or 7602)), "Retired buoy remained in the shipping draw pass.");
            Shot("retired"); _retiredShot = true;
        }
        if (_retiredShot && !Get<bool>("_autoShotScheduled"))
        {
            File.WriteAllText(Path.Combine(output, "complete.txt"), $"{style}: visible held=1 radius=20; hidden held=1 radius=2; unheld=0; retired absent. Actual Mission2 / shipping scene pass.\n");
            Completed = true; game.Exit();
        }
    }
}
