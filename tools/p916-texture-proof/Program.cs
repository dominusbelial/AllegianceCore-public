using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

// Real Game1 flight window, with a deterministic client telemetry fixture after
// loopback logon. Usage: output style model tag off|hd|pbr [preview]
if (args[0] == "--audit") { AtlasAudit.Run(Path.GetFullPath(args[1])); return; }
string output = Path.GetFullPath(args[0]), style = args[1], hull = args[2], tag = args[3];
string diagnostic = args.Length > 4 ? args[4] : "off";
string preview = args.Length > 5 ? args[5] : "";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    Games = [new() { Name = "P916 rendering proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
});
server.Missions[0].Stage = MissionStage.Started;
using var game = new Game1(new Game1.LaunchOptions
{
    DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P916 proof",
    WindowWidth = 1280, WindowHeight = 720, OwnShipPrediction = false,
    EffectsRequested = EffectsQuality.Enhanced, EffectsDetail = EffectsDetail.Medium,
    AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true }
});
game.Content.RootDirectory = content;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(50); } });
ticker.Start();
game.Components.Add(new Proof(game, server.Missions[0].Core, output, style, hull, tag, diagnostic, preview, () => { stop.Cancel(); ticker.Join(); }));
try { game.Run(); } finally { stop.Cancel(); ticker.Join(); }

sealed class Proof(Game1 game, StaticCore core, string output, string style, string hull, string tag, string diagnostic, string preview, Action freeze) : DrawableGameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _frames;
    private bool _shot, _frozen;
    private bool _previewShot;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static void Property(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    public override void Draw(GameTime time)
    {
        if (!_shot || _previewShot || preview != "hangar") return;
        _previewShot = true;
        // Capture the actual production hangar draw before its opaque shop
        // panels hide it. This runs in Game1's graphics context after its frame.
        var gd = game.GraphicsDevice;
        using var rt = new RenderTarget2D(gd, 640, 640, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        Set("_hullPreviewSpin", .9f);
        typeof(Game1).GetMethod("DrawDockedHullPreview", Hidden)!.Invoke(game,
            [new GameTime(time.TotalGameTime, TimeSpan.Zero), Get<ShopModel>("_shopModel")]);
        gd.SetRenderTarget(null);
        using var stream = File.Create(Path.Combine(output, $"{tag}-fighter-{style}-preview.png"));
        rt.SaveAsPng(stream, rt.Width, rt.Height);
    }
    public override void Update(GameTime time)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P916 flight proof timed out");
        if (_shot) { if (!Get<bool>("_autoShotScheduled")) game.Exit(); return; }
        if (Get<object>("_state").ToString() != "Flying") return;
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var world = session.World;
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own) || own.Latest is null
                || (own.StationId >= 0 && (preview != "hangar" || !_frozen))) return;
            if (!_frozen) { freeze(); _frozen = true; }
            var type = hull is "pod" or "station" or "rock" ? core.HullTypes.Values.First(h => h.ModelName == "utl203")
                : hull == "capital" ? core.HullTypes.Values.First(h => h.Name == "Destroyer")
                : core.HullTypes.Values.First(h => h.Name == "Fighter");
            Property(own, "HullId", type.HullId);
            Property(own, "Loadout", hull == "pod" ? Array.Empty<ExpandedPartData>()
                : new[] { new ExpandedPartData(core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Afterburner).PartId, 1, 0, 255) });
            var fixture = new Ship(me, type, new SimVec(0, 5000, 0), SimVec.Zero, Orientation.Identity)
                { StateM = ShipControlState.AfterburnerButton, Controls = new ControlData(0, 0, 0, -1) };
            fixture.SetFraction(1);
            Property(own, "Latest", ServerShipUpdate.FromModel(me, fixture, own.Latest.Time));
            Set("_cameraMode", CameraMode.Chase);
            // Match a player-zoomed chase pose outside the fixed 10-unit near plane.
            // The production default pod camera clips the entire 4-unit hull.
            Get<ChaseCamera>("_chaseCamera").DistanceOverride = hull == "pod" ? 13f : hull is "station" or "rock" ? 350f : null;
            Set("_briefingDismissed", true);
            Get<Allegiance.ClientCore.MonoGame.Flight.DamageFlashTracker>("_flashTracker").ResetBaseline();
            Get<HudSettings>("_hudSettings").Style = style == "classic" ? HudStyle.Classic : HudStyle.Modern;
            var up = Get<UpscalingSettings>("_upscaling");
            up.HdTextures = diagnostic != "off"; up.HdPbr = diagnostic == "pbr"; up.DynamicResolution = false;
            typeof(Game1).GetMethod("SyncHdMaterialMode", Hidden)!.Invoke(game, null);
            // Rendering-only world fixture after the loopback host is paused.
            var stations = (IDictionary)typeof(ClientWorld).GetField("_stations", Hidden)!.GetValue(world)!;
            var rocks = (IDictionary)typeof(ClientWorld).GetField("_asteroids", Hidden)!.GetValue(world)!;
            stations.Clear(); rocks.Clear();
            short sector = own.SectorId;
            if (hull == "station")
            {
                var st = core.StationTypes.Values.First(t => t.ModelName == "ss27");
                stations[(short)900] = new DataStationIGC([0,5000,0], [0,1,0], [0,0,-1], [0,0,0,0], sector, 0, 900, st.StationTypeId, 100, 100, "P916 station");
                Get<ChaseCamera>("_chaseCamera").DistanceOverride = st.Radius * 3.4f;
                if (preview == "preview")
                {
                    var kind = Enum.ToObject(typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)!, 1);
                    var target = Activator.CreateInstance(typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)!, [kind, (short)900, "P916 station", OrderChange.CidNone, false])!;
                    Set("_autoTarget", target);
                }
            }
            if (hull == "rock")
                rocks[(short)901] = new DataAsteroidIGC(1, [0,5000,0], [0,1,0], [0,0,-1], [0,1,0,0], 100,100,1,901,1000,100,"bgrnd03","","heliumrock",sector,"P916 rock",100);
            typeof(Game1).GetMethod("GatherRenderPoses", Hidden)!.Invoke(game, [time, 0]);
            Get<IDictionary>("_damageEmitters").Clear();
            Get<IDictionary>("_engineTrailEmitters").Clear();
            Get<IDictionary>("_nozzleGlowEmitters").Clear();
            // Production's own-ship radius dictionary omits the local hull;
            // its 1-unit fallback would force this capital into the far sprite.
            // Seed the known fixture radius so this proof actually draws its mesh.
            if (hull == "capital") Get<IDictionary>("_shipRadiusById")[me] = .5f * type.Length;
            // Remove unrelated loopback ships so each frame diagnoses this hull.
            var poses = Get<IDictionary>("_poses");
            foreach (object id in poses.Keys.Cast<object>().ToArray()) if (!id.Equals(me)) poses.Remove(id);
            // P920 material evaluation: exercise the actual docked screen and
            // target pane after the same loopback flight/frozen-pose setup.
            if (preview == "hangar")
            {
                Property(own, "StationId", (short)1051);
                Set("_shopModel", new ShopModel(Get<ShopCatalog>("_shopCatalog"), type.HullId, []));
                Set("_dockedActive", true); Set("_hangarOnly", false); Set("_teleportActive", false);
                Set("_hullPreviewSpin", .9f - (float)time.ElapsedGameTime.TotalSeconds);
            }
            if (preview == "target")
            {
                const short targetId = 902;
                var targetShip = new ShipEntity { ShipId = targetId };
                Property(targetShip, "HullId", type.HullId); Property(targetShip, "SideId", own.SideId);
                Property(targetShip, "SectorId", own.SectorId); Property(targetShip, "StationId", (short)-1);
                var targetFixture = new Ship(targetId, type, new SimVec(0, 5000, -1000), SimVec.Zero, Orientation.Identity);
                Property(targetShip, "Latest", ServerShipUpdate.FromModel(targetId, targetFixture, own.Latest.Time));
                ((IDictionary)world.Ships)[targetId] = targetShip;
                var kind = Enum.ToObject(typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)!, 0);
                var target = Activator.CreateInstance(typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)!, [kind, targetId, "P920 fighter", OrderChange.CidNone, false])!;
                Set("_autoTarget", target);
            }
            if (++_frames == 1) Console.WriteLine($"[p916] hull={type.HullId} model={type.ModelName} length={type.Length} fraction={fixture.Fraction} style={style} diagnostic={diagnostic} hd={up.HdTextures} effects={up.Effects}");
            if (_frames < 45) return;
            Console.WriteLine($"[p916] engineEmitters={Get<IDictionary>("_engineTrailEmitters").Count} damageEmitters={Get<IDictionary>("_damageEmitters").Count}");
            Set("_autoshotDir", output); Set("_autoShotName", $"{tag}-{hull}-{style}"); Set("_autoShotScheduled", true); _shot = true;
        }
    }
}
