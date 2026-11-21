// Pure managed proof of the actual browser scene selection/pack contract. No
// Chromium, WASM, graphics, simulation timing or GPU claim is made by this tool.
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
using SimVec = Allegiance.Core.Collision.Vector3;

if (args is ["--audit", var generation, var auditPath])
{
    var resident = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(generation, "join-manifest.json")), StagedJson.Default.StagedManifest)!;
    var manifest = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(generation, "battle-manifest.json")), BattleJson.Default.BattleManifest)!;
    manifest.Validate(resident);
    var assets = resident.Assets.Concat(manifest.Assets).ToDictionary(a => a.Source, StringComparer.OrdinalIgnoreCase);
    foreach (var asset in manifest.Assets)
        await SectorPackStreamer.VerifyCooperativelyAsync(asset, File.ReadAllBytes(Path.Combine(generation, asset.Path)), default);
    var coreAsset = resident.Assets.Single(a => a.Source == "pcore001.igc");
    var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(generation, coreAsset.Path)));
    var failures = new List<string>(); var geometry = new List<object>();
    foreach (var hull in core.HullTypes.Values.DistinctBy(h => h.ModelName))
    {
        if (!assets.TryGetValue(hull.ModelName + ".mdl", out var asset)) { failures.Add(hull.ModelName + ": missing"); continue; }
        try
        {
            var mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(generation, asset.Path)));
            int triangles = mdl.Meshes.Sum(m => m.TriangleCount);
            if (triangles == 0) failures.Add(hull.ModelName + ": no triangles");
            geometry.Add(new { model = hull.ModelName, triangles, unsupported = mdl.UnsupportedGeometryNodes });
        }
        catch (Exception error) { failures.Add(hull.ModelName + ": " + error.GetType().Name); }
    }
    foreach (var missile in core.MissileTypes.Values)
        if (!manifest.Packs.Any(p => p.Key == "missile-" + missile.ExpendableTypeId)) failures.Add("missile ID missing: " + missile.ExpendableTypeId);
    Write(auditPath, new { manifestValidated = true, filesVerified = manifest.Assets.Length, uniqueHullModels = geometry.Count, geometry, failures });
    if (failures.Count > 0) throw new InvalidDataException(string.Join(", ", failures));
    Console.WriteLine($"BATTLE_PACK_AUDIT_PASS files={manifest.Assets.Length} hullModels={geometry.Count}");
    return;
}
if (args is not ["--benchmark", var artwork, var output]) throw new ArgumentException("p1065-battle-proof --audit GENERATION OUTPUT.json | --benchmark ARTWORK OUTPUT.json");
var staticCore = StaticCore.Load(IgcReader.ParseFile(Path.Combine(artwork, "PCore001.igc")));
var hullList = staticCore.HullTypes.Values.ToArray();
var results = new List<object>();
foreach (int perSide in new[] { 1, 5, 10 })
{
    // Also test a roster with 500 known off-sector ships. This measures the
    // render-only gain; it does not simulate or load the CPU outside this pass.
    foreach (int offSector in new[] { 0, 500 })
    {
        var world = new ClientWorld();
        for (short i = 1; i <= perSide * 2 + offSector; i++)
        {
            var hull = hullList[(i * 17) % hullList.Length];
            world.Apply(new ExportShip { ShipId = i, HullId = hull.HullId, SideId = (short)(i % 2), CharacterName = "Ship " + i });
            var entity = world.Ships[i];
            Set(entity, "SectorId", (short)(i <= perSide * 2 ? 1 : 9));
            Set(entity, "PoseSectorId", entity.SectorId); Set(entity, "StationId", (short)-1);
            Set(entity, "State", ShipStatusMessage.StateFlying);
            Set(entity, "Latest", ServerShipUpdate.FromModel(i, new Ship(i, hull, new SimVec(i * 10, 0, 0), SimVec.Zero, Orientation.Identity), 100));
        }
        var scene = new BattleShipScene(staticCore.HullTypes);
        var contacts = new List<HudContact>(32); var names = new List<string>(32);
        int checksum = 0;
        void Before()
        {
            var oldContacts = new List<HudContact>(); var oldNames = new List<string>();
            foreach (var ship in world.Ships.Values)
            {
                if (ship.SectorId != 1 || ship.StationId >= 0 || ship.IsNotVisible || ship.Latest is null) continue;
                var hull = hullList.FirstOrDefault(h => h.HullId == ship.HullId);
                checksum += hull.HullId;
                oldContacts.Add(new(HudContactKind.Ship, default, true, 1, null, false, false, Name: ship.CharacterName));
                oldNames.Add(ship.CharacterName!);
            }
            checksum += oldNames.ToArray().Length + oldContacts.Count;
        }
        void After()
        {
            contacts.Clear(); names.Clear(); scene.Gather(world, 1, null);
            foreach (var ship in scene.Ships)
            {
                checksum += scene.Hull(ship.HullId)!.Value.HullId;
                contacts.Add(new(HudContactKind.Ship, default, true, 1, null, false, false, Name: ship.CharacterName));
                names.Add(ship.CharacterName!);
            }
            checksum += names.Count + contacts.Count;
        }
        for (int i = 0; i < 2000; i++) { Before(); After(); }
        var before = Measure(Before); var after = Measure(After);
        results.Add(new { perSide, inSector = perSide * 2, offSector, before, after, contacts = scene.Ships.Count, checksum });
    }
}
Write(output, new { runtime = ".NET Release JIT; no browser/WASM/GPU claim", tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default", iterations = 20000,
    measuredWork = "Ship selection, hull lookup, contact/name collection only. Excludes projection, prediction and drawing. Before assumes all meshes exist to isolate allocation/lookup cost.", results });
Console.WriteLine("BATTLE_BENCHMARK_PASS " + output);
static object Measure(Action action)
{
    long bytes = GC.GetAllocatedBytesForCurrentThread(), ticks = Stopwatch.GetTimestamp();
    for (int i = 0; i < 20000; i++) action();
    double elapsed = Stopwatch.GetElapsedTime(ticks).TotalMicroseconds;
    long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
    return new { microsecondsPerPass = elapsed / 20000, bytesPerPass = allocated / 20000d };
}
static void Set(ShipEntity ship, string property, object value) => typeof(ShipEntity).GetProperty(property)!.SetValue(ship, value);
static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
