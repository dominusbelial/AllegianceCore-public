// P1069 B2 managed proofs. No browser/GPU timing claim:
//   --benchmark OUT.json   engine-effect preparation cost/allocation, 2/10/20
//                          thrusting ships, full and reduced effect load.
//   --promotion ART OUT    per-frame work at hull promotion: the pre-fix
//                          one-frame decode of every promoted hull vs the
//                          per-frame upload budget of BrowserHullUploads.
using System.Diagnostics;
using System.Reflection;
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

if (args is ["--promotion", var artwork, var promotionOutput])
{
    // Real original hull models from the shipping core: the CPU decode half of
    // the promotion cost (MdlFile.Parse is what NativeShipRenderer.MeshFor does
    // before its GPU buffer build).
    var promotionCore = StaticCore.Load(IgcReader.ParseFile(Path.Combine(artwork, "PCore001.igc")));
    var models = promotionCore.HullTypes.Values.Select(h => h.ModelName).Where(n => n is { Length: > 0 })
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    var frames = new List<object>();
    var decodeMs = new List<double>();
    foreach (string model in models)
    {
        string path = Path.Combine(artwork, model + ".mdl");
        if (!File.Exists(path)) continue;
        byte[] bytes = File.ReadAllBytes(path);
        // Warm one parse so JIT/file cache do not inflate the measured decode.
        _ = MdlFile.Parse(bytes);
        long started = Stopwatch.GetTimestamp();
        var parsed = MdlFile.Parse(bytes);
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        decodeMs.Add(ms);
        frames.Add(new { model, bytes = bytes.Length, meshes = parsed.Meshes.Count, decodeMs = Math.Round(ms, 3) });
    }
    // Same promotion frame, old behaviour: every newly resident hull decoded in
    // one frame. New behaviour: BrowserHullUploads with a per-frame budget.
    var batchRows = new List<object>();
    foreach (int promoted in new[] { 2, 5, 10, 20 })
    {
        var sample = decodeMs.Take(promoted).ToArray();
        if (sample.Length == 0) continue;
        double oneFrame = sample.Sum();
        var queue = new BrowserHullUploads(_ => true, _ => true, perFrameBudget: 1);
        for (short i = 0; i < sample.Length; i++) queue.Observe(i, 0);
        var perFrame = new List<double>();
        for (int frame = 0; frame < sample.Length + 1 && queue.PendingCount > 0; frame++)
        {
            // One queued hull per frame; the reported per-frame cost is that
            // hull's real decode time (the uploadern in the browser also builds
            // its GPU buffers in this same budgeted step).
            int before = queue.PendingCount;
            queue.Step(frame * 16);
            int done = before - queue.PendingCount;
            perFrame.Add(done == 0 ? 0 : sample[frame]);
        }
        batchRows.Add(new
        {
            promoted, oldSingleFrameMs = Math.Round(oneFrame, 3),
            framesToUpload = perFrame.Count, perFrameMs = perFrame.Select(x => Math.Round(x, 3)).ToArray(),
            newMaxPerFrameMs = Math.Round(perFrame.Count == 0 ? 0 : perFrame.Max(), 3),
        });
    }
    Write(promotionOutput, new
    {
        runtime = ".NET Release JIT CPU decode (MdlFile.Parse); GPU buffer/texture upload is the same budgeted step in the browser, not measured here",
        hullModels = models.Length, decoded = frames.Count,
        totalDecodeMs = Math.Round(decodeMs.Sum(), 3), maxSingleHullDecodeMs = Math.Round(decodeMs.DefaultIfEmpty(0).Max(), 3),
        perHull = frames, promotions = batchRows,
        note = "Old promotion decodes every newly resident hull in the frame it is drawn; BrowserHullUploads decodes at most one hull per frame in the update path and the ship keeps its stand-in until then.",
    });
    Console.WriteLine("P1069_PROMOTION_PASS " + frames.Count);
    return;
}
if (args is not ["--benchmark", var pathArtwork, var output])
    throw new ArgumentException("--benchmark ARTWORK OUTPUT | --promotion ARTWORK OUTPUT");
var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(pathArtwork, "PCore001.igc")));
var hulls = core.HullTypes.Values.ToArray();
var results = new List<object>();
foreach (int ships in new[] { 2, 10, 20 })
{
    var world = new ClientWorld();
    // Every fixture ship is an eligible drawn emitter with a boosting wire pose:
    // StateM AfterburnerButton + full throttle, the desktop remote path inputs.
    world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromModel(1,
        new Ship(1, hulls[1], new SimVec(0, 0, 0), new(0, 0, -100), Orientation.Identity)
        { StateM = ShipControlState.AfterburnerButton, Controls = new ControlData(0, 0, 0, 1) }, 100) });
    world.Apply(new ExportShip { ShipId = 1, HullId = hulls[1].HullId, SideId = 0, CharacterName = "Pilot" });
    var fixtures = new List<Ship>();
    for (short i = 2; i <= ships; i++)
    {
        var hull = hulls[(i * 17) % hulls.Length];
        world.Apply(new ExportShip { ShipId = i, HullId = hull.HullId, SideId = (short)(i % 2), CharacterName = "Ship " + i });
        fixtures.Add(new Ship(i, hull, new SimVec(i * 10, 0, 0), new(0, 0, -100), Orientation.Identity)
        { StateM = ShipControlState.AfterburnerButton, Controls = new ControlData(0, 0, 0, 1) });
    }
    world.Apply(HeavyShipsUpdate.FromShips(101, SimVec.Zero, fixtures.ToArray()));
    foreach (var ship in world.Ships.Values)
    {
        var prop = typeof(ShipEntity).GetProperty("StatusSectorKnown")!;
        prop.SetValue(ship, true);
    }
    // Wire Orientation decoding allocates and belongs to the existing prediction
    // path; resolve it before the measured passes (as P1066's proof did).
    var orientations = world.Ships.ToDictionary(p => p.Key, p => p.Value.Latest!.Orientation);
    var scene = new BattleShipScene(core.HullTypes);
    var names = new List<string>(32);
    var random = new RandomTrailSource(1069);
    var b1 = new BrowserSceneEffects();
    var full = new BrowserSceneEffects();
    var reduced = new BrowserSceneEffects();
    var own = world.Ships[1];
    var poses = world.Ships.ToDictionary(p => p.Key, p => p.Value.Latest!);
    double now = 0;
    void Before()
    {
        scene.Gather(world, 1, null); names.Clear();
        for (int i = 0; i < scene.Ships.Count; i++) names.Add(scene.Ships[i].CharacterName!);
        _ = NativeScene.Light(null);
        b1.BeginFrame(world, own, 1 / 60f, now += 1 / 60d);
        for (int i = 0; i < scene.Ships.Count; i++)
        {
            var ship = scene.Ships[i]; var pose = poses[ship.ShipId];
            b1.GatherShip(world, ship, pose.Position, pose.Velocity, orientations[ship.ShipId],
                scene.Hull(ship.HullId), 1 / 60f, (long)(now * 1000));
        }
        b1.FinishParticles(1 / 60f);
    }
    void B2(BrowserSceneEffects effects)
    {
        effects.BeginFrame(world, own, 1 / 60f, now += 1 / 60d);
        for (int i = 0; i < scene.Ships.Count; i++)
        {
            var ship = scene.Ships[i]; var pose = poses[ship.ShipId];
            effects.GatherEngines(world, ship, pose.Position, pose.Velocity, orientations[ship.ShipId],
                scene.Hull(ship.HullId), 1 / 60f);
        }
        for (int i = 0; i < effects.EngineTrailShips.Count; i++)
            effects.Engines.Emit(effects.EngineTrailShips[i], 1 / 60f, random);
    }
    reduced.FullEffectLoad = false;
    for (int i = 0; i < 2000; i++) { Before(); B2(full); B2(reduced); }
    var before = Measure(Before); var fullCost = Measure(() => B2(full)); var reducedCost = Measure(() => B2(reduced));
    results.Add(new { ships, before, b2Full = fullCost, b2Reduced = reducedCost,
        exhaustParticles = full.Engines.ActiveCount, glows = full.Glows.Count, ribbons = full.Motion.RibbonCount,
        reducedExhaust = reduced.Engines.ActiveCount, reducedGlows = reduced.Glows.Count, reducedRibbons = reduced.Motion.RibbonCount });
}
Write(output, new
{
    runtime = ".NET Release JIT, actual browser-source engine preparation; no WASM/GPU claim",
    iterations = 20000,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    before = "P1065 roster selection, cluster light and the P1066 B1 gather in reused buffers",
    b2Full = "Before plus per-ship thrust ramp, exhaust emitter/particle emission, HSB glow value/view and ribbon anchor/colour",
    b2Reduced = "Same preparation with FullEffectLoad=false: exhaust pool cleared and no emission; glow and ribbons stay",
    excluded = "Camera/prediction, original asset decoding, WebGL uploads/drawing, HUD rendering and JavaScript frame pacing",
    results,
});
Console.WriteLine("P1069_BENCHMARK_PASS");
static object Measure(Action action)
{
    long bytes = GC.GetAllocatedBytesForCurrentThread(), ticks = Stopwatch.GetTimestamp();
    for (int i = 0; i < 20000; i++) action();
    double elapsed = Stopwatch.GetElapsedTime(ticks).TotalMicroseconds;
    long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
    return new { microsecondsPerPass = elapsed / 20000, bytesPerPass = allocated / 20000d };
}
static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
