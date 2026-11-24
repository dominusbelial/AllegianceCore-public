using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srv = Allegiance.Server.Server;

// Real Game1 + loopback TCP, following p998-ui-proof's lethal-hull-damage
// fixture. No injected client explosions, replacement renderer or server cheats.
// Usage: p1084-eject-proof <output-directory> <classic|modern> [native|enhanced]
string output = Path.GetFullPath(args.Length > 0 ? args[0] : ".scratch/p1084/desktop");
string style = args.Length > 1 ? args[1] : "classic";
if (style is not ("classic" or "modern")) throw new ArgumentException("Choose classic or modern.");
string quality = args.Length > 2 ? args[2] : "native";
if (quality is not ("native" or "enhanced")) throw new ArgumentException("Choose native or enhanced.");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new Allegiance.Server.ServerConfig
{
    ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    SideSelectAutoSeatSeconds = 30,
    LiveSettings = new() { TestUnlock = new() { Enabled = true, Money = 1_000_000 } },
    Games = [new() { Name = "P1084 eject proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff }],
});
server.Missions[0].Stage = MissionStage.Started;
using var game = new Game1(new Game1.LaunchOptions
{
    DirectHost = System.Net.IPAddress.Loopback.ToString(), DirectPort = server.BoundPort, PlayerName = "P1084 proof pilot",
    WindowWidth = 1280, WindowHeight = 800, EffectsRequested = quality == "native" ? EffectsQuality.Native : EffectsQuality.Enhanced, EffectsDetail = EffectsDetail.High,
    AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true,
        LaunchHull = "Adv Fighter", Camera = "chase" },
});
game.Content.RootDirectory = content;
object simGate = new();
using var stop = new CancellationTokenSource();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        lock (simGate) server.TickSim();
        Thread.Sleep(20);
    }
}) { IsBackground = true };
ticker.Start();
var proof = new Proof(game, server, simGate, output, style, quality);
game.Components.Add(proof);
try { game.Run(); }
finally { stop.Cancel(); ticker.Join(); }
return proof.Passed ? 0 : 1;

sealed class Proof(Game1 game, Srv server, object simGate, string output, string style, string quality) : DrawableGameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step, _frames;
    private double _damageAt;
    private bool _blastCaptured;
    private Vector3 _hullPosition;
    private short _hullId, _podId;
    private int _type;
    private ExplosionSoundTier _tier;
    private object? _receipt;
    public bool Passed { get; private set; }
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P1084 desktop proof timed out.");
        Get<HudSettings>("_hudSettings").Style = style == "classic" ? HudStyle.Classic : HudStyle.Modern;
        Set("_briefingDismissed", true);
        if (_step == 0)
        {
            if (!Get<bool>("_launchSent") || Get<bool>("_dockedActive")) return;
            var session = Get<ConnectionSession>("_session");
            lock (session.Gate)
            {
                if (session.World.MyShipId is not { } id || !session.World.Ships.TryGetValue(id, out var own)
                    || own.Latest is not { } pose) return;
                _hullId = own.HullId;
                _hullPosition = new(pose.Position.X, pose.Position.Y, pose.Position.Z);
            }
            var camera = Get<FreeCam>("_freeCam");
            camera.Position = _hullPosition + new Vector3(0, 30, 100);
            var forward = Vector3.Normalize(_hullPosition - camera.Position);
            camera.Yaw = MathF.Atan2(forward.X, forward.Z); camera.Pitch = MathF.Asin(forward.Y);
            camera.ResetVelocity();
            Set("_freeCamActive", true); Set("_cameraMode", CameraMode.FreeCam);
            _step = 1;
        }
        if (_step >= 1) { Set("_freeCamActive", true); Set("_cameraMode", CameraMode.FreeCam); }
        if (_step == 4)
        {
            var session = Get<ConnectionSession>("_session");
            short? id; lock (session.Gate) id = session.World.MyShipId;
            if (id is { } me && Get<Dictionary<short, Allegiance.ClientCore.MonoGame.Flight.PredictedPose>>("_poses").TryGetValue(me, out var pod))
            {
                var center = new Vector3(pod.Position.X, pod.Position.Y, pod.Position.Z);
                var camera = Get<FreeCam>("_freeCam");
                camera.Position = center + new Vector3(0, 8, 30);
                var forward = Vector3.Normalize(center - camera.Position);
                camera.Yaw = MathF.Atan2(forward.X, forward.Z); camera.Pitch = MathF.Asin(forward.Y);
                camera.ResetVelocity();
            }
        }
        if (_step == 2)
        {
            lock (simGate)
            {
                var session = Get<ConnectionSession>("_session");
                short id; lock (session.Gate) id = session.World.MyShipId!.Value;
                var ship = server.Missions.Select(m => m.GetShip(id)).First(s => s is not null)!;
                _type = ship.HullType.HasCapability(DataHullTypeIGC.HabmFighter) ? ExplosionType.SmallShip : ExplosionType.BigShip;
                _tier = ExplosionSoundTierMapper.ForScale(ship.HullType.Length);
                // Existing p998-ui-proof fixture: ordinary sim damage forces the
                // real server KILL_SHIP(false) + EJECT path over TCP.
                ship.ReceiveDamage(0, 1_000_000, ship.Mission!.LastUpdate, ship.Position, Allegiance.Core.Collision.Vector3.Zero, null);
                _podId = ship.HullType.HullId;
            }
            _damageAt = _watch.Elapsed.TotalSeconds; _step = 3;
        }
    }

    public override void Draw(GameTime gameTime)
    {
        if (_step == 1 && ++_frames >= 8)
        {
            Save("before"); _step = 2; return;
        }
        if (_step is not (3 or 4)) return;
        var session = Get<ConnectionSession>("_session");
        var cues = Get<EjectBlastCues>("_ejectBlasts");
        var draw = Get<List<ExplosionEntity>>("_drawExplosions");
        if (!_blastCaptured && cues.Explosions.Count == 1)
        {
            var blast = cues.Explosions.Keys.Single();
            float age = (Environment.TickCount64 - blast.ReceivedLocalMs) / 1000f;
            if (age < .3f) return; // retain a readable native film frame
            long cueCount = Get<long>("_ejectBlastCueCount"), sounds = Get<long>("_ejectBlastSoundCount");
            int serverBlasts; bool credited;
            lock (session.Gate)
            {
                serverBlasts = session.World.Explosions.Count;
                credited = session.World.Events.KillsAfter(0).Last().BDeathCredit;
            }
            int lights = Get<EffectLights>("_effectLights").Count;
            var director = (DirectorSnapshot)typeof(Game1).GetMethod("BuildDirectorSnapshot", Hidden)!.Invoke(game, [_hullPosition])!;
            int directorBlasts = director.Subjects.Count(s => s.Category == SubjectCategory.Explosion);
            object?[] targetArgs = [null, null];
            bool overrideFound = (bool)typeof(Game1).GetMethod("TryOverrideTargetPos", Hidden)!.Invoke(game, targetArgs)!;
            var point = game.GraphicsDevice.Viewport.Project(new Vector3(blast.Position.X, blast.Position.Y, blast.Position.Z)
                - Get<FreeCam>("_freeCam").Position, Matrix.CreatePerspectiveFieldOfView(
                    MathHelper.ToRadians(Get<float>("_flightFovDeg")), game.GraphicsDevice.Viewport.AspectRatio, 10, 80000),
                Get<FreeCam>("_freeCam").View(), Matrix.Identity);
            _receipt = new { style, quality, hull = _hullId, pod = _podId, deathCredit = credited,
                cueCount, soundRequests = sounds, expectedTier = _tier.ToString(), type = blast.Type,
                position = new[] { blast.Position.X, blast.Position.Y, blast.Position.Z },
                velocity = new[] { blast.Velocity.X, blast.Velocity.Y, blast.Velocity.Z },
                screen = new[] { point.X, point.Y }, age, serverBlasts, drawn = draw.Contains(blast), lights, directorBlasts, overrideFound };
            Passed = !credited && cueCount == 1 && sounds == 1 && blast.Type == _type && serverBlasts == 0
                && draw.Contains(blast) && (quality == "native" || lights > 0) && directorBlasts == 1 && overrideFound && _hullId != _podId;
            Save("blast"); _blastCaptured = true;
            Console.WriteLine($"P1084_BLAST {JsonSerializer.Serialize(_receipt)}");
        }
        if (_watch.Elapsed.TotalSeconds - _damageAt < 7) return;
        if (_step == 3) { _step = 4; return; } // Reframe the surviving pod after expiry.
        if (_watch.Elapsed.TotalSeconds - _damageAt < 8) return;
        Save("pod");
        Passed &= _blastCaptured && cues.Explosions.Count == 0 && Get<long>("_ejectBlastSoundCount") == 1;
        File.WriteAllText(Path.Combine(output, $"proof-{style}.json"), JsonSerializer.Serialize(new { passed = Passed, receipt = _receipt }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"P1084_DESKTOP_{(Passed ? "PASS" : "FAIL")} {style}");
        game.Exit();
    }

    private void Save(string phase)
    {
        var gd = game.GraphicsDevice;
        int width = gd.PresentationParameters.BackBufferWidth, height = gd.PresentationParameters.BackBufferHeight;
        var pixels = new Color[width * height]; gd.GetBackBufferData(pixels);
        using var texture = new Texture2D(gd, width, height); texture.SetData(pixels);
        using var file = File.Create(Path.Combine(output, $"eject-{style}-{phase}.png"));
        texture.SaveAsPng(file, width, height);
    }
}
