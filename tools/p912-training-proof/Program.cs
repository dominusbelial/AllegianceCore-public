using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
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
        Console.WriteLine($"kinematics mass={ship.Mass} thrust={ship.EffectiveThrust()} speed={ship.EffectiveMaxSpeed()} yaw={ship.EffectiveMaxTurnRate(0)} pitch={ship.EffectiveMaxTurnRate(1)} yawTorque={ship.EffectiveTurnTorque(0)} pitchTorque={ship.EffectiveTurnTorque(1)}");
        Console.WriteLine($"preferred={string.Join(',', ship.HullType.PreferredPartsTypes)}");
        foreach (var part in ship.Parts)
            Console.WriteLine($"mounted part={part.PartId} et={part.EquipmentType} mount={part.MountId} amount={part.Amount}");
        for (int slot = 0; slot < ship.CargoHold.Count; slot++)
            if (ship.CargoHold[slot] is { } part) Console.WriteLine($"cargo[{slot}] part={part.PartId} et={part.EquipmentType} amount={part.Amount}");
        foreach (var weapon in ship.Weapons) Console.WriteLine($"mount={weapon.MountId} muzzle={host.Mission.WeaponMuzzles?.Position(ship.HullType, weapon.MountId)} projectile={weapon.ProjectileType.ProjectileTypeId} speed={weapon.ProjectileType.Speed} life={weapon.GetLifespan()} power={weapon.ProjectileType.Power} burst={weapon.GetDtBurst()} damageType={weapon.ProjectileType.DamageType}");
        foreach (var sector in host.Mission.Sectors)
            foreach (var station in sector.StationRuntimes)
                Console.WriteLine($"station={station.StationId} sector={sector.Id} side={station.SideId}");
        if (variant == TrainingHost.EnemyEngagement)
        {
            var miner = host.Mission.Core.HullTypes[338];
            Console.WriteLine($"miner hp={miner.HitPoints} armor={miner.DefenseType} sniperArmorFactor={host.Mission.Core.Constants.DamageConstants[4][miner.DefenseType]} shipDamageGA={ship.GlobalAttributeFactor(Allegiance.Core.Model.GlobalAttributeSet.DamageGuns)}");
        }
        if (variant == TrainingHost.Nanite && host.Mission.Core.ProjectileTypes.TryGetValue(201, out var torpedo))
            Console.WriteLine($"bomber projectile=201 speed={torpedo.Speed} life={torpedo.Lifespan} direct={torpedo.Power} blast={torpedo.BlastPower} radius={torpedo.BlastRadius}");
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
if (args.Length is < 4 or > 6) throw new ArgumentException("window expects content, output, mission, style, optional policy and optional last lesson");
int mission = int.Parse(args[2]);
var style = Enum.Parse<HudStyle>(args[3]);
Directory.CreateDirectory(args[1]);
Environment.SetEnvironmentVariable("ALLEGIANCE_TRAINING_START", mission.ToString());
Environment.SetEnvironmentVariable("ALLEGIANCE_TRAIN_PROOF", "1");
if (args.Length >= 5 && args[4] is "hunt" or "sacrifice")
{
    // Training reserves Insert as AcceptCommand (defaultinputmap.mdl:12-13;
    // TrainingInputGate.NativeDefaultKeyDown). Use a normal per-window user
    // key profile so requested vertical thrust reaches its ordinary handler.
    var keymap = TrainingProofPilot.CreateInputMap();
    string keymapPath = Path.GetFullPath(Path.Combine(args[1], "keymap.json"));
    File.WriteAllText(keymapPath, keymap.ToJson());
    Environment.SetEnvironmentVariable("ALLEGIANCE_KEYMAP", keymapPath);
    Console.WriteLine("[p931] isolated input profile: ThrustUp=NumPad8; native Insert=AcceptCommand retained");
}
if (mission == 2 && args.Length >= 5 && args[4] == "missing-audio")
{
    string source = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    string silentArtwork = Path.GetFullPath(Path.Combine(args[1], "artwork-without-voices"));
    Directory.CreateDirectory(silentArtwork);
    foreach (string entry in Directory.EnumerateFileSystemEntries(source))
    {
        string name = Path.GetFileName(entry);
        if ((name.StartsWith("tm_2_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("tm_slide_2_", StringComparison.OrdinalIgnoreCase))
            && name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) continue;
        string link = Path.Combine(silentArtwork, name);
        if (Directory.Exists(entry)) Directory.CreateSymbolicLink(link, entry);
        else File.CreateSymbolicLink(link, entry);
    }
    Environment.SetEnvironmentVariable(ExternalPaths.ArtworkEnvVar, silentArtwork);
    Console.WriteLine("[p940] M2 voice clips absent; flight artwork retained through a local fixture");
}
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = true, PlayerName = "Training Pilot", WindowWidth = 800, WindowHeight = 480,
    // The shipping --fx native tier keeps software-rendered input responsive.
    // It changes presentation only; host time and combat remain real-time.
    EffectsRequested = mission == 2 || args.Length >= 5 && args[4] is "hunt" or "sacrifice" or "early"
        ? Allegiance.ClientCore.State.EffectsQuality.Native : null,
});
game.Content.RootDirectory = Path.GetFullPath(args[0]);
int lastLesson = args.Length == 6 ? int.Parse(args[5]) : 0;
if (lastLesson < 0 || lastLesson > (mission switch { 2 => 9, 3 => 13, 4 => 10, 5 => 5, 8 => 3, _ => 0 }))
    throw new ArgumentException("Invalid last lesson.");
var proof = new LessonObserver(game, Path.GetFullPath(args[1]), mission, style, args.Length >= 5 ? args[4] : null, lastLesson);
game.Components.Add(proof);
if (args.Length >= 5 && args[4].StartsWith("lesson=", StringComparison.Ordinal))
{
    if (!int.TryParse(args[4][7..], out int lesson) || lesson < 1
        || lesson > (mission switch { 2 => 9, 3 => 13, 4 => 10, 5 => 5, 8 => 3, _ => 0 }))
        throw new ArgumentException("Invalid lesson checkpoint.");
}
else if (args.Length >= 5 && mission == 2)
{
    if (args[4] == "reminders")
        typeof(Game1).GetField("_proofM2Reminders", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, true);
    else if (args[4] is "muted" or "missing-audio")
    {
        typeof(Game1).GetField("_proofM2Audio", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, args[4]);
        if (args[4] == "muted") Microsoft.Xna.Framework.Audio.SoundEffect.MasterVolume = 0;
    }
    else throw new ArgumentException("unknown M2 policy");
}
else if (args.Length >= 5)
{
    string field = args[4] switch { "early" => "_proofNaniteMountEarly", "loss" => "_proofNaniteWithholdRepair", "departure" => "_proofM4WaitForDeparture", "hunt" => "_proofM4HuntMiner", "sacrifice" => "_proofNaniteSacrifice", _ => throw new ArgumentException("unknown policy") };
    typeof(Game1).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, true);
}
// Each real window records the exact binaries it loaded, including windows
// launched later by the long training gate.
File.WriteAllText(Path.Combine(args[1], "build.json"), System.Text.Json.JsonSerializer.Serialize(
    new[] { Assembly.GetExecutingAssembly(), typeof(Game1).Assembly, typeof(TrainingClock).Assembly,
        typeof(Allegiance.Core.Sim.Ship).Assembly }.Distinct().ToDictionary(a => Path.GetFileName(a.Location),
            a => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(a.Location))).ToLowerInvariant()),
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
game.Run();
if (!proof.Completed) throw new InvalidOperationException("The lesson window exited before the requested proof completed.");
if (lastLesson > 0 || args.Length >= 5 && args[4].StartsWith("lesson=", StringComparison.Ordinal))
    Console.WriteLine($"P912_LESSON_PASS M{mission} {style} {args[4]}");
else Console.WriteLine($"P912_WINDOW_PASS M{mission} {style} lessons={string.Join(',', proof.Lessons.Order())}");

sealed class LessonObserver(Game1 game, string output, int missionId, HudStyle style, string? policy, int lastLesson = 0) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public readonly HashSet<int> Lessons = new();
    public bool Completed { get; private set; }
    private bool _seeded, _started;
    private readonly int _stopAfterLesson = lastLesson > 0 ? lastLesson
        : policy?.StartsWith("lesson=", StringComparison.Ordinal) == true ? int.Parse(policy[7..]) : 0;
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
    private readonly Dictionary<int, int> _minerLastSector = new();
    private readonly HashSet<int> _minersKilledInMars = new();
    private bool _minerKillShot;
    private bool _combinedAnnouncementSeen;
    private bool _bothDestroyed;
    private bool _outpostDestroyed;
    private bool _bomberDestroyed;
    private int _endingMessageStart = -1;
    private string[] _endingMessages = [];
    private double _nextBasicFlightTrace;
    private bool _combatRecipeWritten;
    private bool _naniteLeadShot;
    private double _nextCombatTrace;
    private string _lastGoal = "";
    private StreamWriter? _pilotTrace;
    private TrainingSession? _basicFlightSession;
    private string[] _basicFlightPrompts = [];
    private TimeSpan _pilotFrame = TimeSpan.MinValue;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pilotTrace?.Dispose();
        base.Dispose(disposing);
    }

    private void TracePilot(GameTime frame, TrainingHost host, string goal)
    {
        if (policy is not ("hunt" or "sacrifice")) return;
        // Game1 also pumps components from UpdateFlying. Count each rendered
        // frame once when measuring the sampling cadence.
        if (_pilotFrame == frame.TotalGameTime) return;
        _pilotFrame = frame.TotalGameTime;
        _pilotTrace ??= new StreamWriter(Path.Combine(output, "pilot.csv")) { AutoFlush = true };
        if (_pilotTrace.BaseStream.Position == 0)
            _pilotTrace.WriteLine("wall,frameDt,sim,sampleDt,goal,target,sector,hull,shield,distance,ownError,aim,leadAim,yaw,pitch,fire,keys,outpostHull,outpostShield,ownX,ownY,ownZ,hostX,hostY,hostZ,forecastError");
        var target = host.GetCommandTarget(host.PlayerShipId);
        var own = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        var controls = Get<Allegiance.Core.Sim.ControlData>("_lastSentControls");
        var keys = Get<Microsoft.Xna.Framework.Input.Keys[]?>("_proofKeys") ?? [];
        var ship = target is { Type: ObjectType.Ship } t && host.Drones.TryGetValue(t.Id, out var s) ? s : null;
        var delta = ship is null ? Allegiance.Core.Collision.Vector3.Zero : ship.Position - own.Position;
        float aim = delta.LengthSquared() > 0 ? MathF.Acos(Math.Clamp(own.Orientation.GetForward() * (delta / delta.Length()), -1, 1)) * 180 / MathF.PI : 0;
        var point = Get<System.Numerics.Vector3?>("_proofAimPoint");
        var leadDelta = point is { } p ? new Allegiance.Core.Collision.Vector3(p.X, p.Y, p.Z) - own.Position : delta;
        float leadAim = leadDelta.LengthSquared() > 0 ? MathF.Acos(Math.Clamp(own.Orientation.GetForward() * leadDelta.Normalize(), -1, 1)) * 180 / MathF.PI : 0;
        var hostPosition = host.PlayerShip.Position;
        var forecast = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_proofForecast");
        var station = missionId == 8 ? host.Mission.GetStation(Mission8.EnemyOutpostStationId) : null;
        _pilotTrace.WriteLine(FormattableString.Invariant($"{_watch.Elapsed.TotalSeconds:F4},{frame.ElapsedGameTime.TotalSeconds:F4},{host.Mission.LastUpdate:F4},{Get<float>("_proofSampleSeconds"):F4},{goal},{target?.Id},{ship?.Sector?.Id},{ship?.Fraction:F5},{ship?.ShieldFraction:F5},{delta.Length():F3},{(own.Position - hostPosition).Length():F3},{aim:F3},{leadAim:F3},{controls.Yaw:F5},{controls.Pitch:F5},{(host.PlayerShip.StateM & Allegiance.Core.Sim.ShipControlState.WeaponsMask) != 0},{string.Join('|', keys)},{station?.ArmorFraction:F5},{station?.ShieldFraction:F5},{own.Position.X:F3},{own.Position.Y:F3},{own.Position.Z:F3},{hostPosition.X:F3},{hostPosition.Y:F3},{hostPosition.Z:F3},{(forecast.Position - own.Position).Length():F3}"));
    }

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
        else
        {
            // P988: named test styles must select the actual bound renderer,
            // not merely change a preference while another skin draws.
            var active = (IHudSkinRenderer)typeof(Game1).GetProperty("ActiveHud", Hidden)!.GetValue(game)!;
            Check(active.Id == (style == HudStyle.Classic ? HudSkinIds.Classic : HudSkinIds.Modern),
                "Requested training skin is not the active renderer.");
        }
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
            TracePilot(gameTime, host, goal);
            if (missionId == 2)
            {
                _basicFlightSession = session;
                // Keep the real landing receipt before ReleaseTraining disposes the host.
                _basicFlightPrompts = session.Site.Messages.ToArray();
                if (_watch.Elapsed.TotalSeconds >= _nextBasicFlightTrace)
                {
                    _nextBasicFlightTrace = _watch.Elapsed.TotalSeconds + 5;
                    var instructor = host.CommanderShip;
                    Console.WriteLine($"[p940] FLIGHT goal={goal} wall={_watch.Elapsed.TotalSeconds:F3} clock={Get<double>("_trainingClock"):F3} host={host.Mission.LastUpdate:F3} frame={gameTime.ElapsedGameTime.TotalSeconds:F3} instructor={instructor.Position} velocity={instructor.Velocity} live={host.Mission.GetShip(instructor.Id) is not null} dead={instructor.Dead} hp={instructor.Fraction} station={instructor.StationId} sector={instructor.Sector?.Id} accepted={instructor.AcceptedShipCommandId}:{instructor.AcceptedShipTarget?.Id} current={instructor.CurrentCommandId}:{instructor.CommandTarget?.Id} plan={instructor.PlanCommandId}:{instructor.PlanCommandTarget?.Id} buoys={string.Join('|', host.Waypoints.Keys.Select(id => $"{id}:{host.BuoyConsumerCount(id)}"))}");
                }
            }
            var messages = ((ClientTrainingSite)session.Site).Messages;
            if (missionId == 8)
            {
                if (goal is "3.19" or "3.20" && _endingMessageStart < 0) _endingMessageStart = messages.Count;
                if (_endingMessageStart >= 0) _endingMessages = messages.Skip(_endingMessageStart).ToArray();
                if (goal == "3.19" && !_combatRecipeWritten)
                {
                    _combatRecipeWritten = true;
                    var station = host.Mission.GetStation(Mission8.EnemyOutpostStationId)!;
                    var lines = new List<string> { FormattableString.Invariant($"station armor={station.EffectiveMaxArmor()} shield={station.EffectiveMaxShield()} armorRegen={station.EffectiveArmorRegen()} shieldRegen={station.EffectiveShieldRegen()}") };
                    foreach (int id in Enumerable.Range(8004, 8))
                        if (host.Drones.TryGetValue(id, out var actor))
                            lines.Add(FormattableString.Invariant($"ship={id} hull={actor.HullType.HullId} hp={actor.HullType.HitPoints} damageGA={actor.GlobalAttributeFactor(Allegiance.Core.Model.GlobalAttributeSet.DamageGuns)} guns={string.Join(';', actor.Weapons.Select(w => $"{w.ProjectileType.ProjectileTypeId}:power={w.ProjectileType.Power}:burst={w.Type.DtimeBurst}:range={w.ProjectileType.Speed * w.GetLifespan()}:damageType={w.ProjectileType.DamageType}"))}"));
                    File.WriteAllLines(Path.Combine(output, "combat-recipe.txt"), lines);
                }
            }
            if ((missionId == 4 && goal.StartsWith("7.", StringComparison.Ordinal)
                || missionId == 8 && goal.StartsWith("3.", StringComparison.Ordinal))
                && _watch.Elapsed.TotalSeconds >= _nextCombatTrace)
            {
                _nextCombatTrace = _watch.Elapsed.TotalSeconds + .2;
                int[] ids = missionId == 4 ? [1101, 1102, 1103, 1104] : [8007, 8008, 8009, 8010, 8011];
                foreach (int id in ids)
                    if (host.Drones.TryGetValue(id, out var drone))
                        File.AppendAllText(Path.Combine(output, "combat.csv"),
                            FormattableString.Invariant($"{_watch.Elapsed.TotalSeconds:F3},{goal},{id},{drone.Dead},{drone.Sector?.Id},{drone.Fraction:F5},{drone.ShieldFraction:F5},{drone.Position.X:F2},{drone.Position.Y:F2},{drone.Position.Z:F2},{drone.AcceptedShipCommandId},{drone.AcceptedShipTarget?.Id}\n"));
                if (missionId == 8)
                    File.AppendAllText(Path.Combine(output, "combat.csv"),
                        FormattableString.Invariant($"{_watch.Elapsed.TotalSeconds:F3},{goal},1051,{host.GetFraction(new(ObjectType.Station, Mission8.EnemyOutpostStationId)) <= 0},1052,{host.GetFraction(new(ObjectType.Station, Mission8.EnemyOutpostStationId)):F5},{host.Mission.GetStation(Mission8.EnemyOutpostStationId)?.ShieldFraction ?? 0:F5}\n"));
            }
            if (missionId == 4)
            {
                // Native mission4.cpp:821-824: destruction of ANY miner ends
                // this goal, independently of the currently selected target.
                foreach (int id in Enumerable.Range(1101, 4))
                    if (host.TrainingShipExists(id) && host.Drones.TryGetValue(id, out var liveMiner))
                        _minerLastSector[id] = liveMiner.Sector?.Id ?? -1;
                    else if (_minerLastSector.GetValueOrDefault(id) == 1031
                        && host.Drones.TryGetValue(id, out var deadMiner) && deadMiner.Dead && deadMiner.Fraction <= 0)
                        _minersKilledInMars.Add(id);
                if (policy == "hunt" && !_minerKillShot && _minersKilledInMars.Count > 0)
                {
                    _minerKillShot = true;
                    Shot($"m4-{style.ToString().ToLowerInvariant()}-miner-killed");
                }
                if (policy == "hunt" && goal == "7.7" && _minersKilledInMars.Count == 0)
                    Check(!Enumerable.Range(1101, 4).All(id => host.TrainingShipExists(id)
                        && host.Drones.TryGetValue(id, out var escaped) && escaped.Sector?.Id != 1031),
                        "all four living miners left Mars without a kill; the local hunting window was missed");
            }
            if (missionId == 8 && !_combinedAnnouncementSeen
                && messages.Contains("Our bomber got destroyed too, but that is an acceptable price to pay."))
            {
                _combinedAnnouncementSeen = true;
                Shot($"m8-{style.ToString().ToLowerInvariant()}-both-destroyed");
                Console.WriteLine("[p917] COMBINED_ANNOUNCEMENT exact native message observed");
            }
            if (missionId == 8 && !_naniteLeadShot && goal == "3.19"
                && policy is "early" or "sacrifice"
                && host.GetCommandTarget(host.PlayerShipId) is { Type: ObjectType.Ship, Id: 8007 }
                && (host.PlayerShip.StateM & Allegiance.Core.Sim.ShipControlState.WeaponsMask) != 0)
            {
                _naniteLeadShot = true;
                Shot($"m8-{style.ToString().ToLowerInvariant()}-nanite-lead");
            }
            // Capture the order BEFORE the locked liveness read. Retirement
            // between these reads must be classified as death, not as a replay
            // changing a live combatant's authored order.
            if (missionId == 8 && goal == "3.19" && host.Drones.TryGetValue(8007, out var commander))
            {
                bool attack = commander.AcceptedShipCommandId == TrainingMissionIds.CmdAttack
                    && commander.AcceptedShipTarget?.Id == Mission8.EnemyOutpostStationId;
                if (host.TrainingShipExists(8007)
                    && host.GetFraction(new(ObjectType.Station, Mission8.EnemyOutpostStationId)) > 0)
                    Check(attack, "input replay replaced the authored bomber attack order");
            }
            if (missionId == 4 && !_quickShot && Get<bool>("_qcMenuOpen"))
            { _quickShot = true; Shot($"m4-{style.ToString().ToLowerInvariant()}-quickcomm"); }
            if (missionId == 8 && !_commandShot && Get<CameraMode>("_cameraMode") == CameraMode.Command
                && Get<bool>("_trainingCommandPane") && Get<PaneSlideTracker>("_paneSlides").Progress(SlidingPane.CommandPane, Get<double>("_nowSeconds")) >= .9f)
            { _commandShot = true; Shot($"m8-{style.ToString().ToLowerInvariant()}-command-bar"); }
            if (missionId == 4 && goal == "7.7")
                // Native mission4.cpp:823 tests CURRENT target, not the last
                // ship selected before switching to a local aleph.
                _minerTarget = host.GetCommandTarget(host.PlayerShipId) is { Type: ObjectType.Ship } target ? target.Id : -1;
            if (goal != _lastGoal && missionId == 5 && goal == "5.5") Shot($"m5-{style.ToString().ToLowerInvariant()}-team");
            if (goal != _lastGoal && missionId == 8 && goal == "1.14") Shot($"m8-{style.ToString().ToLowerInvariant()}-repair");
            if (missionId == 8 && goal == "3.22")
            {
                bool outpostDead = host.GetFraction(new(ObjectType.Station, Mission8.EnemyOutpostStationId)) <= 0;
                bool bomberDead = !host.TrainingShipExists(8007)
                    && host.GetFraction(new(ObjectType.Ship, 8007)) <= 0;
                _outpostDestroyed = outpostDead;
                _bomberDestroyed = bomberDead;
                Check(outpostDead || bomberDead, "ending without a real destroyed bomber or outpost");
                if (!_outcomeChecked || outpostDead && bomberDead && !_bothDestroyed)
                    Console.WriteLine($"[p912] OUTCOME minerSurvived={_minerSurvived} outpostDestroyed={outpostDead} bomberDestroyed={bomberDead}");
                _bothDestroyed |= outpostDead && bomberDead;
                if (policy == "loss") Check(bomberDead && !outpostDead, "withheld repair did not exercise bomber-loss/outpost-survives branch");
                // Early mounting pins miner survival in lesson 2, not an
                // outpost win in lesson 3. Either final combat ending is native
                // (mission8.cpp:600-654); exact ending messages are checked below.
                else if (policy == "early") Check(_minerSurvived, "early mounting did not preserve the miner");
                if (policy != "sacrifice" && !_outcomeChecked)
                    Check(messages.Contains("Enemy outpost destroyed.") || messages.Contains("The bomber got destroyed, better luck next time."), "missing exact native ending announcement");
                if (_bothDestroyed)
                {
                    foreach (int defender in new[] { 8009, 8010, 8011 })
                        if (host.TrainingShipExists(defender))
                            Check(host.HasCommand(defender, TrainingMissionIds.CmdDoNothing), "native ending did not stop a surviving enemy defender");
                }
                _outcomeChecked = true;
            }
            if (!int.TryParse(goal.Split('.')[0], out int major)) return;
            if (!_started)
            {
                _started = true;
                Check(host.PlayerShip.HullType.HullId == missionId switch { 2 => 412, 3 => 414, 4 => 486, 5 => 412, 8 => 685, _ => 0 }, "native starting hull");
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
                    bool departed = _minerTarget >= 0 && host.TrainingShipExists(_minerTarget)
                        && host.Drones.TryGetValue(_minerTarget, out var miner) && miner.Sector?.Id != 1031;
                    Check(killed || departed, "lesson 7 ended without a miner kill or target sector departure");
                    if (policy == "departure") Check(!killed && departed, "did not exercise the non-kill departure branch");
                    if (policy == "hunt")
                    {
                        Check(_minersKilledInMars.Count > 0, "did not kill a miner before it left Mars");
                        Check(messages.Contains("Target an enemy miner with the F key, and destroy it if you can."), "missing exact native miner prompt");
                        Check(!departed, "selected miner departed before the kill branch");
                    }
                    Console.WriteLine($"[p912] M4_BRANCH minerKilled={killed} targetDeparted={departed} killedInMars={string.Join(',', _minersKilledInMars.Order())}");
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
                    // The death message belongs only to the preceding combat
                    // goal. A projectile can kill the miner after that goal,
                    // when native explicitly has no death-message action
                    // (training/mission8.cpp:385-392). Its verdict voice still
                    // follows at :394-401; the loss window asserts tm_8_15.
                    if (_minerSurvived)
                        Check(messages.Any(m => m.Contains("Yee-haaaaaw!", StringComparison.Ordinal)), "missing miner survival announcement");
                    Console.WriteLine($"[p912] M8_MINER_OUTCOME survived={_minerSurvived} deathMessage={messages.Any(m => m.Contains("Our miners are dead!", StringComparison.Ordinal))}");
                    if (policy == "loss")
                    {
                        Check(!_minerSurvived, "withheld repair did not exercise miner loss");
                        Check(Get<int>("_proofLossDiversionMask") == 7, "loss pilot did not divert all three defenders through real orders");
                    }
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
            if (missionId == 2 && !Completed)
            {
                Check(_basicFlightSession is { Completed: true, Finished: true }, "native landing did not finish mission");
                var connection = Get<ConnectionSession>("_session");
                lock (connection.Gate)
                {
                    var world = connection.World;
                    Check(world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own)
                        && own.StationId == 1010, "trainee did not really dock at station 1010");
                }
                Check(_basicFlightPrompts.SequenceEqual(new[] {
                    "Press the SPACEBAR when you are ready to proceed.",
                    "Press BACKSLASH (\\) or set your joystick throttle to maximum for full thrust.",
                    "Use your scroll wheel or joystick throttle to adjust thrust.",
                    "Press the SPACEBAR when you are ready to proceed.",
                    "Press and hold the SPACE key to fire the booster." }), "native prompts missing or out of order");
                var voices = Get<HashSet<string>>("_proofM2Voices");
                if (policy != "missing-audio")
                    Check(new[] { "tm_2_01", "tm_2_08", "tm_2_20", "tm_2_23", "tm_2_26", "tm_2_38" }.All(voices.Contains), "installed narration never played through real audio");
                File.WriteAllLines(Path.Combine(output, "voices.txt"), voices.Order());
                var stages = Get<HashSet<string>>("_proofM2Stages");
                Check(new[] { "intro-locked", "turn-only", "throttle-only", "forced-throttle", "cleared-locked", "all-axes", "scripted-booster", "player-booster", "booster-disabled" }.All(stages.Contains), "missing a native control stage");
                if (policy is null or "reminders")
                {
                    var controls = Get<HashSet<string>>("_proofM2Observed");
                    foreach (string stage in new[] { "intro-locked", "forced-throttle", "cleared-locked" })
                        foreach (string probe in new[] { "keyboard-turn", "mouse-turn" })
                            Check(controls.Contains(stage + "/" + probe), $"missing locked control input: {stage}/{probe}");
                    foreach (string stage in new[] { "turn-only", "throttle-only", "all-axes" })
                        foreach (string probe in new[] { "keyboard-turn", "mouse-turn", "throttle-zero", "throttle-up", "throttle-down", "wheel-up", "wheel-down", "reset-between-backslashes", "restore-stop", "space" })
                            Check(controls.Contains(stage + "/" + probe), $"missing control input: {stage}/{probe}");
                    foreach (string stage in new[] { "turn-only", "all-axes" })
                        foreach (string probe in new[] { "full-iso", "full-us" })
                            Check(controls.Contains(stage + "/" + probe), $"missing backslash variant: {stage}/{probe}");
                }
                File.WriteAllLines(Path.Combine(output, "controls.txt"), Get<List<string>>("_proofM2Receipts"));
                Console.WriteLine("[p940] M2_NATIVE_END docked=1010 completed=True prompts=5 stages=9");
            }
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
            if (missionId == 8)
            {
                Check(_outcomeChecked, "final native outcome was not observed");
                if (policy == "sacrifice")
                    Check(_bothDestroyed, "did not exercise the combined bomber/outpost loss branch");
                if (_bothDestroyed)
                {
                    // Native mission8.cpp:646-650 checks throughout the final
                    // five seconds, including a late projectile's bomber kill.
                    Check(_combinedAnnouncementSeen, "missing exact native combined-loss announcement (mission8.cpp:650)");
                    const string win = "Enemy outpost destroyed.", loss = "The bomber got destroyed, better luck next time.",
                        cheer = "Yee-haaaaaw!", combined = "Our bomber got destroyed too, but that is an acceptable price to pay.";
                    // mission8.cpp:633,641,648,650 are independent, once-only
                    // conditions. A bomber-first loss can be followed by an
                    // outpost kill during either announcement goal.
                    Check(new[] { new[] { win, cheer, combined }, new[] { loss, win, cheer, combined },
                        new[] { loss, cheer, combined } }.Any(_endingMessages.SequenceEqual),
                        "combined ending messages differ from native order/text (mission8.cpp:633,648,650)");
                }
                else if (_outpostDestroyed)
                    Check(_endingMessages.SequenceEqual(new[] { "Enemy outpost destroyed.", "Yee-haaaaaw!" }), "outpost-win ending differs from native");
                else
                    Check(_bomberDestroyed && _endingMessages.SequenceEqual(new[] { "The bomber got destroyed, better luck next time." }), "bomber-loss ending differs from native");
            }
            Check(Lessons.Count == missionId switch { 2 => 9, 3 => 13, 4 => 10, 5 => 5, 8 => 3, _ => 0 }, "did not observe every lesson");
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
