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
// loopback logon. No production sim/wire changes. Usage: output style hull tag [clean|no-soft]
string output = Path.GetFullPath(args[0]), style = args[1], hull = args[2], tag = args[3];
string diagnostic = args.Length > 4 ? args[4] : "";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    Games = [new() { Name = "P915 rendering proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
});
server.Missions[0].Stage = MissionStage.Started;
using var game = new Game1(new Game1.LaunchOptions
{
    DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P915 proof",
    WindowWidth = 1280, WindowHeight = 720, OwnShipPrediction = false,
    EffectsRequested = EffectsQuality.Enhanced, EffectsDetail = EffectsDetail.Medium,
    AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true }
});
game.Content.RootDirectory = content;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(50); } });
ticker.Start();
game.Components.Add(new Proof(game, server.Missions[0].Core, output, style, hull, tag, diagnostic, () => { stop.Cancel(); ticker.Join(); }));
try { game.Run(); } finally { stop.Cancel(); ticker.Join(); }

sealed class Proof(Game1 game, StaticCore core, string output, string style, string hull, string tag, string diagnostic, Action freeze) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _frames;
    private bool _shot, _frozen;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static void Property(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    public override void Update(GameTime time)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P915 flight proof timed out");
        if (_shot) { if (!Get<bool>("_autoShotScheduled")) game.Exit(); return; }
        if (Get<object>("_state").ToString() != "Flying") return;
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var world = session.World;
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own) || own.Latest is null || own.StationId >= 0) return;
            if (!_frozen) { freeze(); _frozen = true; }
            var type = hull == "pod" ? core.HullTypes.Values.First(h => h.ModelName == "utl203")
                : core.HullTypes.Values.First(h => h.Name == "Fighter");
            Property(own, "HullId", type.HullId);
            Property(own, "Loadout", hull == "pod" ? Array.Empty<ExpandedPartData>()
                : new[] { new ExpandedPartData(core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Afterburner).PartId, 1, 0, 255) });
            var fixture = new Ship(me, type, new SimVec(0, 5000, 0), SimVec.Zero, Orientation.Identity)
                { StateM = ShipControlState.AfterburnerButton, Controls = new ControlData(0, 0, 0, -1) };
            fixture.SetFraction(diagnostic == "clean" ? 1 : .45f);
            Property(own, "Latest", ServerShipUpdate.FromModel(me, fixture, own.Latest.Time));
            Set("_cameraMode", CameraMode.Chase);
            // Match a player-zoomed chase pose outside the fixed 10-unit near plane.
            // The production default pod camera clips the entire 4-unit hull.
            Get<ChaseCamera>("_chaseCamera").DistanceOverride = hull == "pod" ? 13f : null;
            Set("_briefingDismissed", true);
            Get<Allegiance.ClientCore.MonoGame.Flight.DamageFlashTracker>("_flashTracker").ResetBaseline();
            Get<HudSettings>("_hudSettings").Style = style == "classic" ? HudStyle.Classic : HudStyle.Modern;
            var up = Get<UpscalingSettings>("_upscaling");
            up.HdTextures = true; up.HdPbr = diagnostic == "pbr"; up.DynamicResolution = false;
            typeof(Game1).GetMethod("SyncHdMaterialMode", Hidden)!.Invoke(game, null);
            if (diagnostic == "no-soft") Set("_softEnabled", false);
            typeof(Game1).GetMethod("GatherRenderPoses", Hidden)!.Invoke(game, [time, 0]);
            if (diagnostic is "white" or "unflip")
            {
                Get<HdMaterialRenderer>("_hdMaterials").Mode = HdDrawMode.Off;
                var renderer = Get<NativeShipRenderer>("_nativeShipRenderer");
                var parsed = MdlFile.Parse(File.ReadAllBytes(Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), type.ModelName + ".mdl")));
                foreach (string stem in parsed.Meshes.Select(m => m.TextureStem).Append(renderer.TextureStemFor(type.ModelName, type.TextureName)).OfType<string>().Distinct())
                {
                    var texture = renderer.TextureFor(stem)!;
                    if (diagnostic == "white") texture.SetData(Enumerable.Repeat(Color.White, texture.Width * texture.Height).ToArray());
                    else texture.SetData(MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), stem + ".mdl")), flipVertical: false).Rgba);
                }
                if (_frames == 0)
                    foreach (var mesh in parsed.Meshes)
                    {
                        var normals = Enumerable.Range(0, mesh.VertexCount).Select(i => new Vector3(mesh.Normals[3*i], mesh.Normals[3*i+1], mesh.Normals[3*i+2]).Length()).ToArray();
                        Console.WriteLine($"[p915-mesh] texture={mesh.TextureStem} triangles={mesh.TriangleCount} normalLength={normals.Min()}..{normals.Max()}");
                    }
            }
            if (diagnostic is "white" or "unflip" or "hull-only")
            {
                Get<IDictionary>("_damageEmitters").Clear();
                Get<IDictionary>("_engineTrailEmitters").Clear();
                Get<IDictionary>("_nozzleGlowEmitters").Clear();
            }
            // Remove unrelated loopback ships so each frame diagnoses this hull.
            var poses = Get<IDictionary>("_poses");
            foreach (object id in poses.Keys.Cast<object>().ToArray()) if (!id.Equals(me)) poses.Remove(id);
            if (++_frames == 1) Console.WriteLine($"[p915] hull={type.HullId} model={type.ModelName} length={type.Length} fraction={fixture.Fraction} style={style} diagnostic={diagnostic} hd={up.HdTextures} effects={up.Effects}");
            if (_frames < 120) return;
            Console.WriteLine($"[p915] engineEmitters={Get<IDictionary>("_engineTrailEmitters").Count} damageEmitters={Get<IDictionary>("_damageEmitters").Count}");
            Set("_autoshotDir", output); Set("_autoShotName", $"{tag}-{hull}-{style}"); Set("_autoShotScheduled", true); _shot = true;
        }
    }
}
