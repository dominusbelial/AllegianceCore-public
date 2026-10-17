using Allegiance.Core;
using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
var art = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")!;
var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(art, "m2xspiderw.igc")));
var mission = Mission.Assemble(core, map, [new SideSpec(0, "A", 25), new SideSpec(1, "B", 3)], new NullIgcSite());
for (int tick = 0; tick < 100; tick++) foreach (var side in mission.Sides) side.Update(tick * 0.05);
const int ticks = 2000;
long before = GC.GetAllocatedBytesForCurrentThread();
var watch = Stopwatch.StartNew();
for (int tick = 100; tick < 100 + ticks; tick++) foreach (var side in mission.Sides) side.Update(tick * 0.05);
watch.Stop();
Console.WriteLine(JsonSerializer.Serialize(new { ticks, sides = mission.Sides.Count,
    buckets = mission.Sides.Sum(s => s.Buckets.Count), milliseconds = watch.Elapsed.TotalMilliseconds,
    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before }));
