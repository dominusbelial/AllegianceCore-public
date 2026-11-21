// Managed interpreter-source preparation proof. No browser/GPU timing claim.
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

if (args is ["--audit", var art, var generation, var auditOutput])
{
    var resident = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(generation, "join-manifest.json")), StagedJson.Default.StagedManifest)!;
    var index = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(generation, "battle-manifest.json")), BattleJson.Default.BattleManifest)!;
    index.Validate(resident);
    var sceneSources = index.Packs.Where(p => p.Key.StartsWith("scene-", StringComparison.Ordinal))
        .SelectMany(p => p.Sources).Distinct().ToArray();
    var assets = resident.Assets.Concat(index.Assets).ToDictionary(a => a.Source, StringComparer.OrdinalIgnoreCase);
    var originals = Directory.EnumerateFiles(art, "*", SearchOption.AllDirectories)
        .GroupBy(p => Path.GetRelativePath(art, p), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    var rows = new List<object>();
    foreach (string source in sceneSources)
    {
        var asset = assets[source];
        byte[] bytes = File.ReadAllBytes(Path.Combine(generation, asset.Path));
        await SectorPackStreamer.VerifyCooperativelyAsync(asset, bytes, default);
        bool identical = bytes.AsSpan().SequenceEqual(File.ReadAllBytes(originals[source]));
        if (!identical) throw new InvalidDataException("Non-original scene bytes: " + source);
        rows.Add(new { source, asset.ByteSize, asset.Sha256, originalBytes = identical });
    }
    Write(auditOutput, new { validated = true, files = rows.Count, originals = rows });
    Console.WriteLine("SCENE_AUDIT_PASS " + rows.Count);
    return;
}
if (args is not ["--benchmark", var artwork, var output])
    throw new ArgumentException("--benchmark ARTWORK OUTPUT | --audit ARTWORK GENERATION OUTPUT");
var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(artwork, "PCore001.igc")));
var hulls = core.HullTypes.Values.ToArray();
var results = new List<object>();
foreach (int ships in new[] { 2, 10, 20 })
foreach (int offSector in new[] { 0, 500 })
foreach (bool fullEffects in new[] { true, false })
{
    var world = new ClientWorld();
    for (short i = 1; i <= ships + offSector; i++)
    {
        var hull = hulls[(i * 17) % hulls.Length];
        world.Apply(new ExportShip { ShipId = i, HullId = hull.HullId, SideId = (short)(i % 2), CharacterName = "Ship " + i });
        var ship = world.Ships[i];
        Set(ship, "SectorId", (short)(i <= ships ? 1 : 9));
        Set(ship, "PoseSectorId", ship.SectorId); Set(ship, "StationId", (short)-1);
        Set(ship, "State", ShipStatusMessage.StateFlying); Set(ship, "StatusSectorKnown", true);
        Set(ship, "Latest", ServerShipUpdate.FromModel(i,
            new Ship(i, hull, new SimVec(i * 10, 0, 0), new(0, 0, -100), Orientation.Identity), 100));
    }
    world.Apply(new ExportWarp { Warp = new(10, 20, 25, "plnt19", "", "Gate", [0, 0, -100], [0, 0, 1], [0, 0, 0, 0], 1, 1) });
    // Rendering already resolved these poses before B1 preparation. Wire
    // Orientation decoding itself allocates and belongs to existing prediction.
    var orientations = world.Ships.ToDictionary(p => p.Key, p => p.Value.Latest!.Orientation);
    var scene = new BattleShipScene(core.HullTypes); var effects = new BrowserSceneEffects();
    var dust = new SubtleDust(); var debris = new DebrisGeo(1, new RandomTrailSource(1066));
    var views = new List<AmbientDustView>(300); var options = new VisualOptions();
    var projection = Matrix.CreatePerspectiveFieldOfView(1, 16 / 9f, 1, 10000);
    var frustum = new BoundingFrustum(projection);
    var cluster = default(DataClusterIGC) with { LightDirection = [3, 0, 4], LightColor = 0xFF804020 };
    var contacts = new List<HudContact>(32); var names = new List<string>(32);
    var own = world.Ships[1]; double now = 0; int checksum = 0;
    void Before()
    {
        scene.Gather(world, 1, null); contacts.Clear(); names.Clear();
        for (int i = 0; i < scene.Ships.Count; i++)
        {
            var ship = scene.Ships[i]; checksum += scene.Hull(ship.HullId)!.Value.HullId;
            contacts.Add(new(HudContactKind.Ship, default, true, 1, null, false, false, Name: ship.CharacterName));
            names.Add(ship.CharacterName!);
        }
    }
    void After()
    {
        Before(); now += 1 / 60d;
        var lighting = NativeScene.Light(cluster); checksum += (int)(lighting.KeyColor.X * 10);
        effects.BeginFrame(world, own, 1 / 60f, now);
        for (int i = 0; i < scene.Ships.Count; i++)
        {
            var ship = scene.Ships[i]; var pose = ship.Latest!;
            _ = NativeScene.Opacity(true, now);
            effects.GatherShip(world, ship, pose.Position, pose.Velocity, orientations[ship.ShipId], scene.Hull(ship.HullId), 1 / 60f, (long)(now * 1000));
        }
        effects.FinishParticles(1 / 60f);
        effects.EndFrame(own, own.Latest!.Position, own.Latest.Velocity, orientations[own.ShipId], 12);
        effects.GatherGlimpses(world, Vector3.Zero);
        if (fullEffects) NativeScene.GatherDust(options, dust, debris, default, Vector3.Zero,
            Matrix.Identity, projection, now, views, frustum);
    }
    for (int i = 0; i < 2000; i++) { Before(); After(); }
    var before = Measure(Before); var after = Measure(After);
    results.Add(new { ships, offSector, fullEffects, before, after, dustGrains = views.Count,
        strobes = effects.Strobes.Count, sparks = effects.Sparks.Count, checksum });
}
Write(output, new { runtime = ".NET Release JIT, actual browser-source preparation; no WASM/GPU claim", iterations = 20000,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    before = "P1065 indexed roster, hull lookup, contacts/names in reused buffers",
    after = "Before plus cluster light, cloak, drawn-ship strobes, live aleph lead-up sparks/flash/pulse, glimpse selection and default dust preparation/culling",
    excluded = "Camera/prediction, original asset decoding, WebGL uploads/drawing, HUD rendering and JavaScript frame pacing",
    results });
Console.WriteLine("SCENE_BENCHMARK_PASS");
static object Measure(Action action)
{
    long bytes = GC.GetAllocatedBytesForCurrentThread(), ticks = Stopwatch.GetTimestamp();
    for (int i = 0; i < 20000; i++) action();
    double elapsed = Stopwatch.GetElapsedTime(ticks).TotalMicroseconds;
    long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
    return new { microsecondsPerPass = elapsed / 20000, bytesPerPass = allocated / 20000d };
}
static void Set(ShipEntity ship, string property, object value) => typeof(ShipEntity).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(ship, value);
static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
