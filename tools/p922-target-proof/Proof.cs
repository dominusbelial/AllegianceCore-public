using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

// Uses actual Game1 art resolution/cache, target gate, HUD composition and shared
// material renderer. Reflection is confined to this deterministic proof fixture.
sealed class Proof(Game1 game, StaticCore core, string output, bool noPack, Action freeze) : DrawableGameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<object> _rows = [];
    private readonly Dictionary<string, Color[]> _images = [];
    private int _phase, _frames;
    private bool _frozen;
    private object? _heldArt;
    private static readonly string[] Kinds = ["ship", "station", "asteroid", "file-row"];
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static void Property(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private string Kind => Kinds[_phase / 6];
    private HudStyle Style => _phase % 6 / 3 == 0 ? HudStyle.Classic : HudStyle.Modern;
    private HdDrawMode Mode => (HdDrawMode)(_phase % 3);

    public override void Update(GameTime time)
    {
        if (_watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("P922 proof timed out");
        if (_phase == 24) { game.Exit(); return; }
        if (Get<object>("_state").ToString() != "Flying") return;
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var world = session.World;
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own) || own.Latest is null || own.StationId >= 0) return;
            if (!_frozen) { freeze(); _frozen = true; using var timer = GlFrameTimer.TryCreate(); Console.WriteLine("P922_RENDERER " + timer?.Renderer); }
            Get<HudSettings>("_hudSettings").Style = Style;
            var up = Get<UpscalingSettings>("_upscaling");
            up.HdTextures = Mode != HdDrawMode.Off; up.HdPbr = Mode == HdDrawMode.Pbr; up.DynamicResolution = false;
            typeof(Game1).GetMethod("SyncHdMaterialMode", Hidden)!.Invoke(game, null);
            Set("_briefingDismissed", true);
            if (_frames != 0) { _frames++; return; }
            var type = core.HullTypes.Values.First(h => h.Name == "Fighter");
            Property(own, "HullId", type.HullId);
            var fixture = new Ship(me, type, new SimVec(0, 5000, 0), SimVec.Zero, Orientation.Identity);
            fixture.SetFraction(1);
            Property(own, "Latest", ServerShipUpdate.FromModel(me, fixture, own.Latest.Time));
            Set("_cameraMode", CameraMode.Chase);
            var stations = (IDictionary)typeof(ClientWorld).GetField("_stations", Hidden)!.GetValue(world)!;
            var rocks = (IDictionary)typeof(ClientWorld).GetField("_asteroids", Hidden)!.GetValue(world)!;
            stations.Clear(); rocks.Clear();
            short sector = own.SectorId;
            int targetKind = 0;
            if (Kind is "station" or "file-row")
            {
                var st = core.StationTypes.Values.First(t => t.ModelName == (Kind == "station" ? "ss27" : "ss102"));
                stations[(short)902] = new DataStationIGC([0,5000,-1500], [0,1,0], [0,0,-1], [0,0,0,0], sector, 0, 902, st.StationTypeId, 100, 100, "P922 " + Kind);
                targetKind = 1;
            }
            else if (Kind == "asteroid")
            {
                rocks[(short)902] = new DataAsteroidIGC(1, [0,5000,-1500], [0,1,0], [0,0,-1], [0,1,0,0], 100,100,1,902,1000,100,"bgrnd03","","heliumrock",sector,"P922 asteroid",100);
                targetKind = 2;
            }
            else
            {
                var ship = new ShipEntity { ShipId = 902 };
                Property(ship, "HullId", type.HullId); Property(ship, "SideId", own.SideId);
                Property(ship, "SectorId", sector); Property(ship, "StationId", (short)-1);
                var target = new Ship(902, type, new SimVec(0,5000,-1000), SimVec.Zero, Orientation.Identity);
                Property(ship, "Latest", ServerShipUpdate.FromModel(902, target, own.Latest.Time));
                ((IDictionary)world.Ships)[(short)902] = ship;
            }
            // Preserve target and cached art for the subsequent five style/mode phases.
            if (_phase % 6 == 0)
            {
                var kind = Enum.ToObject(typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)!, targetKind);
                Set("_autoTarget", Activator.CreateInstance(typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)!,
                    [kind, (short)902, "P922 " + Kind, OrderChange.CidNone, false])!);
                _heldArt = null;
            }
            typeof(Game1).GetMethod("GatherRenderPoses", Hidden)!.Invoke(game, [time, 0]);
            Get<IDictionary>("_damageEmitters").Clear(); Get<IDictionary>("_engineTrailEmitters").Clear(); Get<IDictionary>("_nozzleGlowEmitters").Clear();
            _frames = 1;
        }
    }

    public override void Draw(GameTime time)
    {
        if (_frames < 12 || _phase >= 24) return;
        var renderer = Get<TargetModelPreviewRenderer>("_targetModelPreviewRenderer");
        var preview = renderer.Target ?? throw new Exception("No target preview for " + Kind);
        var cache = Get<object>("_previewCache");
        var art = cache.GetType().GetProperty("Art")!.GetValue(cache)!;
        Check(art is not null, "Art missing " + Kind);
        if (_heldArt is not null) Check(ReferenceEquals(art, _heldArt), "Mode/style switch re-resolved art");
        _heldArt = art;
        string model = Field<string>(art!, "ModelStem"), stem = Field<string>(art!, "TextureStem");
        Check(!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(stem), "Lost material identity " + Kind);
        var pixels = new Color[256 * 256]; preview.GetData(pixels);
        Check(pixels.Count(p => p.A != 0) > 100, "Empty preview " + Kind);
        string id = $"{Kind}-{Style}-{Mode}";
        using (var stream = File.Create(Path.Combine(output, id + "-pane.png"))) preview.SaveAsPng(stream, 256, 256);
        var gd = game.GraphicsDevice;
        int width = gd.PresentationParameters.BackBufferWidth, height = gd.PresentationParameters.BackBufferHeight;
        var full = new Color[width * height]; gd.GetBackBufferData(full);
        using (var texture = new Texture2D(gd, width, height))
        { texture.SetData(full); using var stream = File.Create(Path.Combine(output, id + ".png")); texture.SaveAsPng(stream, width, height); }
        VerifyAndMeasure(renderer, art!, model, stem, out double cpuMs, out double completedMs, out long allocated, out int oldCallDifferentPixels, out Color[] canonical);
        int diffOff = 0, diffClassic = 0;
        if (Mode != HdDrawMode.Off)
        {
            diffOff = canonical.Zip(_images[$"{Kind}-{Style}-{HdDrawMode.Off}"]).Count(pair => pair.First != pair.Second);
            Check(noPack ? diffOff == 0 : diffOff > 100, "Unexpected HD/native difference " + id);
        }
        if (Mode == HdDrawMode.Pbr)
        {
            diffClassic = canonical.Zip(_images[$"{Kind}-{Style}-{HdDrawMode.Classic}"]).Count(pair => pair.First != pair.Second);
            Check(noPack ? diffClassic == 0 : diffClassic > 100, "Unexpected PBR/classic difference " + id);
        }
        _images[id] = canonical;
        _rows.Add(new { kind = Kind, style = Style.ToString(), mode = Mode.ToString(), model, texture = stem, diffOff, diffClassic, oldCallDifferentPixels, cpuMs, completedMs, allocated });
        Console.WriteLine($"P922 {id} {model}/{stem} diffOff={diffOff} diffClassic={diffClassic} submitMs={cpuMs:F4} completedMs={completedMs:F4} alloc={allocated}");
        _phase++; _frames = 0;
        if (_phase == 24)
        {
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(_rows, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("P922_GPU_PASS cases=24");
        }
    }

    private void VerifyAndMeasure(TargetModelPreviewRenderer renderer, object art, string model, string stem,
        out double cpuMs, out double completedMs, out long allocated, out int oldCallDifferentPixels, out Color[] canonical)
    {
        var gd = game.GraphicsDevice;
        var world = Matrix.Identity; var view = TargetModelPreview.View(new(0,0,-1000), Vector3.Up, Vector3.Forward);
        var proj = TargetModelPreview.Projection(); var light = TargetModelPreview.Lighting(new(0,0,-1000));
        var tex = Field<Texture2D?>(art, "Texture"); float scale = Field<float>(art, "NativeScale");
        bool station = Kind is "station" or "file-row";
        var ships = Get<NativeShipRenderer>("_nativeShipRenderer");
        var stations = Get<NativeStationRenderer>("_nativeStationRenderer");
        var rocks = Get<NativeAsteroidRenderer>("_nativeAsteroidRenderer");
        var hd = Get<HdMaterialRenderer>("_hdMaterials");
        var shipGeo = station ? null : Field<NativeShipRenderer.NativeShipGeometry>(art, "ShipGeo");
        var stationGeo = station ? Field<NativeStationRenderer.NativeStationGeometry>(art, "StationGeo") : null;
        void Pane(bool omit = false)
        {
            if (station) renderer.RenderStation(stationGeo!, tex, world, view, proj, scale, light, null, omit ? null : model, omit ? null : stem);
            else if (Kind == "asteroid") renderer.RenderAsteroid(shipGeo!, tex, world, view, proj, scale, light, null, omit ? null : model, omit ? null : stem);
            else renderer.RenderShip(shipGeo!, tex, world, view, proj, scale, light, null, omit ? null : model, omit ? null : stem);
        }
        var expected = new Color[256 * 256]; var actual = new Color[expected.Length];
        Pane(); renderer.Target!.GetData(expected);
        canonical = (Color[])expected.Clone();
        Check(gd.BlendState == BlendState.Opaque && gd.DepthStencilState == DepthStencilState.Default && gd.GetRenderTargets().Length == 0, "End failed state restore");
        // The world renderer with identical camera/lighting is the independent
        // parameter-level oracle for both model and texture stem forwarding.
        using var rt = new RenderTarget2D(gd, 256,256,false,SurfaceFormat.Color,DepthFormat.Depth24);
        gd.SetRenderTarget(rt); gd.Clear(Color.Transparent);
        if (station) stations.Draw(Field<NativeStationRenderer.NativeStationGeometry>(art, "StationGeo"), tex, world, view, proj, scale, light, model, stem);
        else if (Kind == "asteroid") rocks.Draw(Field<NativeShipRenderer.NativeShipGeometry>(art, "ShipGeo"), tex, world, view, proj, scale, light, model, stem);
        else ships.Draw(Field<NativeShipRenderer.NativeShipGeometry>(art, "ShipGeo"), tex, world, view, proj, 1, scale, light, model, stem);
        gd.SetRenderTarget(null); rt.GetData(actual);
        Check(expected.SequenceEqual(actual), "Preview/world material parameters differ");
        Pane(omit:true); renderer.Target!.GetData(actual);
        oldCallDifferentPixels = expected.Zip(actual).Count(pair => pair.First != pair.Second);
        if (Mode == HdDrawMode.Off || noPack)
            Check(oldCallDifferentPixels == 0, "Native fallback differs from pre-fix draw");
        // Parameter cases: identical native mesh/atlas with per-part identity
        // versus identity inherited only from the file row. Each renderer must
        // give identical pixels for these two representations. Omitting the
        // stems (the old call) must lose Classic for inherited parts, and PBR
        // for both. This isolates the parameters from the artwork's own scopes.
        var mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), model + ".mdl")));
        Console.WriteLine($"P922_SCOPES {model} parts={mdl.Meshes.Count} inherited={mdl.Meshes.Count(m => m.TextureStem is null)} texture={stem}");
        if (Mode != HdDrawMode.Off)
        {
            Color[]? ownPixels = null;
            foreach (bool inherited in new[] { false, true })
            {
                var meshes = mdl.Meshes.Select(m => new MdlMesh { Positions=m.Positions, Normals=m.Normals,
                    TexCoords=m.TexCoords, Indices=m.Indices, TextureStem=inherited ? null : stem }).ToArray();
                using var sg = station ? null : NativeShipRenderer.NativeShipGeometry.Build(gd, meshes);
                using var stg = station ? NativeStationRenderer.NativeStationGeometry.Build(gd, meshes) : null;
                var originalShip = shipGeo; var originalStation = stationGeo;
                shipGeo = sg; stationGeo = stg;
                Pane(); renderer.Target!.GetData(expected);
                if (ownPixels is null) ownPixels = (Color[])expected.Clone();
                else Check(ownPixels.SequenceEqual(expected), "Inherited texture parameter differs from own stem " + Kind);
                Pane(omit:true); renderer.Target!.GetData(actual);
                bool shouldDiffer = !noPack && (Mode == HdDrawMode.Pbr || inherited);
                Check(expected.SequenceEqual(actual) != shouldDiffer, "Old-call regression guard failed " + Kind + "/" + inherited);
                shipGeo = originalShip; stationGeo = originalStation;
            }
        }
        for (int i=0; i<8; i++) Pane();
        long before = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
        for (int i=0; i<40; i++) Pane();
        cpuMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 40;
        allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 40;
        start = Stopwatch.GetTimestamp();
        for (int i=0; i<12; i++) { Pane(); renderer.Target!.GetData(actual); }
        completedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 12;
        Check(hd.Mode == Mode, "Preview changed shared HD mode");
    }
}
