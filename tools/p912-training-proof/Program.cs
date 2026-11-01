using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Training;
using Allegiance.ClientCore.Session;
using Allegiance.SinglePlayer;
using Allegiance.Core;
using Microsoft.Xna.Framework;

if (args is ["universe"])
{
    foreach (var variant in new[] { TrainingHost.BasicWeaponry, TrainingHost.EnemyEngagement, TrainingHost.Nanite })
    {
        using var host = TrainingHost.Start(variant);
        var ship = host.PlayerShip;
        Console.WriteLine($"{variant.GameName} hull={ship.HullType.HullId} price={ship.HullType.Price}");
        Console.WriteLine($"preferred={string.Join(',', ship.HullType.PreferredPartsTypes)}");
        foreach (var part in ship.Parts.Concat(ship.CargoHold.OfType<Allegiance.Core.Sim.MountedPart>()))
            Console.WriteLine($"part={part.PartId} et={part.EquipmentType} mount={part.MountId} amount={part.Amount} price={part.Type.Price}");
        foreach (var weapon in ship.Weapons) Console.WriteLine($"speed={weapon.ProjectileType.Speed} life={weapon.GetLifespan()}");
        foreach (short id in ship.HullType.PreferredPartsTypes.Where(id => id >= 0))
        {
            if (!host.Mission.Core.PartTypes.TryGetValue(id, out var part) && !host.Mission.Core.TryBuildExpendableLauncher(id, out part)) continue;
            var upgraded = Allegiance.Core.Model.SuccessorUpgrade.PartSuccessor(host.Mission.Core, part,
                host.Mission.GetStation(host.HomeStationId)!.Type.TtbmLocal, host.Mission.Sides[0].DevelopmentTechs);
            Console.WriteLine($"preferred part {id} -> {upgraded.PartId} {upgraded.Name} price={upgraded.Price} et={upgraded.EquipmentType} mask={upgraded.PartMask} canMount={ship.HullType.CanMountPart(upgraded, 0)} pm={string.Join(',', ship.HullType.PmEquipment)}");
        }
    }
    return;
}

if (args is not ["window", ..])
    throw new ArgumentException("usage: p912-training-proof window <content> <output> <mission> <Classic|Modern> [early|loss|departure|hunt|sacrifice|lesson=N]");
args = args[1..];
if (args.Length is < 4 or > 5) throw new ArgumentException("window expects content, output, mission, style and an optional policy");
int mission = int.Parse(args[2]);
var style = Enum.Parse<HudStyle>(args[3]);
Directory.CreateDirectory(args[1]);
Environment.SetEnvironmentVariable("ALLEGIANCE_TRAINING_START", mission.ToString());
Environment.SetEnvironmentVariable("ALLEGIANCE_TRAIN_PROOF", "1");
using var game = new Game1(new Game1.LaunchOptions { Intro = true, PlayerName = "Training Pilot", WindowWidth = 800, WindowHeight = 480 });
game.Content.RootDirectory = Path.GetFullPath(args[0]);
var proof = new LessonObserver(game, Path.GetFullPath(args[1]), mission, style, args.Length == 5 ? args[4] : null);
game.Components.Add(proof);
if (args.Length == 5 && args[4].StartsWith("lesson=", StringComparison.Ordinal))
{
    if (!int.TryParse(args[4][7..], out int lesson) || lesson < 1
        || lesson > (mission switch { 3 => 13, 4 => 10, 5 => 5, 8 => 3, _ => 0 }))
        throw new ArgumentException("Invalid lesson checkpoint.");
}
else if (args.Length == 5)
{
    string field = args[4] switch { "early" => "_proofNaniteMountEarly", "loss" => "_proofNaniteWithholdRepair", "departure" => "_proofM4WaitForDeparture", "hunt" => "_proofM4HuntMiner", "sacrifice" => "_proofNaniteSacrifice", _ => throw new ArgumentException("unknown policy") };
    typeof(Game1).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, true);
}
game.Run();
if (!proof.Completed) throw new InvalidOperationException("The lesson window exited before full completion.");
if (args.Length == 5 && args[4].StartsWith("lesson=", StringComparison.Ordinal))
    Console.WriteLine($"P912_LESSON_PASS M{mission} {style} {args[4]}");
else Console.WriteLine($"P912_WINDOW_PASS M{mission} {style} lessons={string.Join(',', proof.Lessons.Order())}");

sealed class LessonObserver(Game1 game, string output, int missionId, HudStyle style, string? policy) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public readonly HashSet<int> Lessons = new();
    public bool Completed { get; private set; }
    private bool _seeded, _started;
    private readonly int _stopAfterLesson = policy?.StartsWith("lesson=", StringComparison.Ordinal) == true ? int.Parse(policy[7..]) : 0;
    private bool _prefixStopped;
    private bool _quickShot;
    private bool _commandShot;
    private int _lastMajor;
    private double _nextShot = 25;
    private int _probesSeen;
    private bool _heightOrderSeen;
    private bool _minerSurvived;
    private bool _outcomeChecked;
    private int _minerTarget = -1;
    private string _lastGoal = "";

    private T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private void Set(string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException($"M{missionId} {_lastGoal}: {message}"); }
    private void Shot(string name)
    { Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true); }
    public override void Update(GameTime gameTime)
    {
        if (_prefixStopped)
        {
            if (!Get<bool>("_autoShotScheduled")) game.Exit();
            return;
        }
        if (File.Exists(Path.Combine(output, "cancel"))) throw new OperationCanceledException("Window proof cancelled by its owner.");
        if (_watch.Elapsed.TotalSeconds > 1200) throw new TimeoutException($"M{missionId} stalled at {_lastGoal}");
        if (!_seeded) { Get<HudSettings>("_hudSettings").Style = style; _seeded = true; }
        if (Get<TrainingSession?>("_trainingSession") is { Started: true } session
            && Get<TrainingHost?>("_embeddedHost") is { } host)
        {
            if (_watch.Elapsed.TotalSeconds > _nextShot)
            { _nextShot += 15; Shot($"m{missionId}-{style.ToString().ToLowerInvariant()}-current"); }
            if (missionId == 5) _probesSeen = Math.Max(_probesSeen, host.GetProbeCount());
            if (missionId == 5 && host.Drones.TryGetValue(3007, out var layer)
                && layer.AcceptedShipTarget is Allegiance.Core.Sim.Buoy buoy && MathF.Abs(buoy.Position.Z) > 10)
            {
                if (!_heightOrderSeen) Console.WriteLine($"[p912] HEIGHT_ORDER buoy={buoy.Id} z={buoy.Position.Z:F2}");
                _heightOrderSeen = true;
            }
            string goal = session.Mission.ActiveGoalPath;
            if (missionId == 8 && goal == "3.19" && host.Drones.TryGetValue(8007, out var commander))
                Check(commander.AcceptedShipCommandId == TrainingMissionIds.CmdAttack
                    && commander.AcceptedShipTarget?.Id == Mission8.EnemyOutpostStationId,
                    "input replay replaced the authored bomber attack order");
            if (missionId == 4 && !_quickShot && Get<bool>("_qcMenuOpen"))
            { _quickShot = true; Shot($"m4-{style.ToString().ToLowerInvariant()}-quickcomm"); }
            if (missionId == 8 && !_commandShot && Get<CameraMode>("_cameraMode") == CameraMode.Command
                && Get<bool>("_trainingCommandPane") && Get<PaneSlideTracker>("_paneSlides").Progress(SlidingPane.CommandPane, Get<double>("_nowSeconds")) >= .9f)
            { _commandShot = true; Shot($"m8-{style.ToString().ToLowerInvariant()}-command-bar"); }
            if (missionId == 4 && goal == "7.7" && host.GetCommandTarget(host.PlayerShipId) is { Type: ObjectType.Ship } target)
                _minerTarget = target.Id;
            if (goal != _lastGoal && missionId == 5 && goal == "5.5") Shot($"m5-{style.ToString().ToLowerInvariant()}-team");
            if (goal != _lastGoal && missionId == 8 && goal == "1.14") Shot($"m8-{style.ToString().ToLowerInvariant()}-repair");
            if (missionId == 8 && goal == "3.22")
            {
                bool outpostDead = host.GetFraction(new(ObjectType.Station, Mission8.EnemyOutpostStationId)) <= 0;
                bool bomberDead = !host.TrainingShipExists(8007);
                Check(outpostDead || bomberDead, "ending without a real destroyed bomber or outpost");
                if (!_outcomeChecked)
                    Console.WriteLine($"[p912] OUTCOME minerSurvived={_minerSurvived} outpostDestroyed={outpostDead} bomberDestroyed={bomberDead}");
                if (policy == "loss") Check(bomberDead && !outpostDead, "withheld repair did not exercise bomber-loss/outpost-survives branch");
                else Check(outpostDead, "repair escort did not destroy the outpost");
                if (policy == "sacrifice") Check(bomberDead, "did not exercise the combined bomber/outpost loss branch");
                var finalMessages = ((ClientTrainingSite)session.Site).Messages;
                Check(finalMessages.Any(m => m.Contains(outpostDead ? "Enemy outpost destroyed." : "The bomber got destroyed, better luck next time.", StringComparison.Ordinal)), "missing native ending announcement");
                _outcomeChecked = true;
            }
            if (!int.TryParse(goal.Split('.')[0], out int major)) return;
            if (!_started)
            {
                _started = true;
                Check(host.PlayerShip.HullType.HullId == missionId switch { 3 => 414, 4 => 486, 5 => 412, 8 => 685, _ => 0 }, "native starting hull");
                if (missionId == 3)
                {
                    Check(host.PlayerShip.CargoHold.Select(p => p!.PartId).SequenceEqual(new short[] { 14, 25, 25, 24, 24 }), "native starting cargo differs");
                    Check(!Get<bool>("_groupFire") && Get<int>("_selectedMount") == 0, "native M3 starting single-weapon UI differs");
                }
                if (missionId == 8)
                {
                    Check(host.PlayerShip.CargoHold[0]?.PartId == 190, "Nanite2 missing in cargo -1");
                    Check(host.PlayerShip.CargoHold.Skip(1).All(p => p?.PartId == 59), "spare proximity mines missing");
                }
            }
            if (goal != _lastGoal)
            {
                Console.WriteLine($"[p912] M{missionId} goal={goal} elapsed={_watch.Elapsed.TotalSeconds:F1}");
                File.AppendAllText(Path.Combine(output, "goals.csv"), $"{missionId},{style},{goal},{_watch.Elapsed.TotalSeconds:F3}\n");
                _lastGoal = goal;
            }
            if (major != _lastMajor)
            {
                if (missionId == 4 && major == 3)
                {
                    Check(Get<bool>("_proofM4QuickOpened") && Get<bool>("_proofM4QuickSent"), "native tilde menu was not opened and used");
                    Check(Get<bool>("_groupFire"), "quickcomm's numeric selection leaked into the weapon controls");
                }
                if (missionId == 4 && major == 8)
                {
                    bool killed = Enumerable.Range(1101, 4).Any(id => !host.TrainingShipExists(id));
                    bool departed = _minerTarget >= 0 && host.Drones.TryGetValue(_minerTarget, out var miner) && miner.Sector?.Id != 1031;
                    Check(killed || departed, "lesson 7 ended without a miner kill or target sector departure");
                    if (policy == "departure") Check(!killed && departed, "did not exercise the non-kill departure branch");
                    if (policy == "hunt") Check(killed, "did not exercise the miner-kill branch");
                    Console.WriteLine($"[p912] M4_BRANCH minerKilled={killed} targetDeparted={departed}");
                }
                if (missionId == 4 && major == 9)
                    Check(!host.TrainingShipExists(1100), "constructor defense ended without a real scout kill");
                if (missionId == 4 && major == 10)
                    Check(host.Mission.Sectors.SelectMany(s => s.StationRuntimes).Any(s => s.SideId == 0 && s.ClusterId == 1031 && !s.Dead), "constructor never built the Mars expansion");
                if (missionId == 8 && _lastMajor == 1)
                {
                    Check(host.GetFraction(new(ObjectType.Station, Mission8.HomeStationId)) >= 1, "station was not repaired fully");
                    bool prompted = ((ClientTrainingSite)session.Site).Messages.Any(m => m.Contains("Swap the Nanite Repair System", StringComparison.Ordinal));
                    Check(prompted == (policy != "early"), "did not exercise the expected early/late mounting branch");
                    Console.WriteLine($"[p912] M8_MOUNT prompted={prompted}");
                }
                if (missionId == 8 && _lastMajor == 2)
                {
                    _minerSurvived = host.TrainingShipExists(8001);
                    var messages = ((ClientTrainingSite)session.Site).Messages;
                    Check(messages.Any(m => m.Contains(_minerSurvived ? "Yee-haaaaaw!" : "Our miners are dead!", StringComparison.Ordinal)), "missing miner branch announcement");
                    if (policy == "loss") Check(!_minerSurvived, "withheld repair did not exercise miner loss");
                    if (policy == "early") Check(_minerSurvived, "early mounting and conserved repair energy did not exercise miner survival");
                    Console.WriteLine($"[p912] miners-survived={_minerSurvived}");
                }
                if (missionId == 3 && major == 10)
                    Check(Get<ConsolePaneState>("_consolePanes").Inventory, "lesson 10 did not open real inventory");
                if (missionId == 3 && major == 11)
                    Check(host.PlayerShip.Fraction == 1 && host.PlayerShip.Ammo == host.PlayerShip.HullType.MaxAmmo, "lesson 11 did not rearm real ship");
                if (missionId == 5 && major == 5)
                    Check(!host.TrainingShipExists(3001), "enemy scout was not destroyed by combat");
                if (_stopAfterLesson > 0 && _lastMajor == _stopAfterLesson && major > _stopAfterLesson)
                {
                    Completed = _prefixStopped = true;
                    File.WriteAllText(Path.Combine(output, "complete.txt"), $"M{missionId} {style}: lesson {_stopAfterLesson} completed through its native prefix\n");
                    Shot($"m{missionId}-{style.ToString().ToLowerInvariant()}-lesson{_stopAfterLesson}-complete");
                    return;
                }
                Lessons.Add(major); _lastMajor = major;

                Shot($"m{missionId}-{style.ToString().ToLowerInvariant()}-lesson{major}");
            }

        }
        else if (_started && Get<bool>("_trainingSlideshowIsPost"))
        {
            if (missionId == 4)
            {
                // Teardown has already disposed the embedded host. The real
                // DOCKED wire state remains in the connection's final world.
                var connection = Get<ConnectionSession>("_session");
                lock (connection.Gate)
                {
                    var world = connection.World;
                    Check(world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own)
                        && world.Stations.TryGetValue(own.StationId, out var station) && station.ClusterId == 1031,
                        "trainee did not dock at the real Mars expansion");
                }
            }
            if (missionId == 5)
            {
                Check(_probesSeen >= 3, "three tower orders never produced three real probes");
                Check(_heightOrderSeen, "height drag never produced a real elevated buoy order");
            }
            if (missionId == 8) Check(_outcomeChecked, "final native outcome was not observed");
            Check(Lessons.Count == missionId switch { 3 => 13, 4 => 10, 5 => 5, 8 => 3, _ => 0 }, "did not observe every lesson");
            if (!Completed)
            {
                Completed = true;
                File.WriteAllText(Path.Combine(output, "complete.txt"), $"M{missionId} {style}: all {Lessons.Count} lessons completed in one session\n");
                Shot($"m{missionId}-{style.ToString().ToLowerInvariant()}-complete");
            }
            else if (!Get<bool>("_autoShotScheduled")) game.Exit();
        }
    }
}
