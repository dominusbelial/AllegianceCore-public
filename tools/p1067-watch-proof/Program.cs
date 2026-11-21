// P1067 — pure managed proof of the browser Watch snapshot/camera seam: the
// per-frame allocation claim, the snapshot's subject mix, the shot-change cadence
// and seed determinism. No Chromium, WASM, graphics or GPU claim is made here.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

if (args is not ["--benchmark", var output])
{
    Console.Error.WriteLine("usage: p1067-watch-proof --benchmark <out.json>");
    return 2;
}

var hull = default(DataHullTypeIGC) with
{
    HullId = 1, ModelName = "fig21", Length = 24, Mass = 1,
    MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1], Hardpoints = [], PmEquipment = new short[8],
};

ClientWorld BattleWorld(int sectorShips, int offSectorShips)
{
    var world = new ClientWorld();
    world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromModel(1,
        new Ship(1, hull, new SimVec(0, 0, 0), SimVec.Zero, Orientation.Identity), 100) });
    world.Apply(new ExportShip { ShipId = 1, HullId = 1, SideId = 0, CharacterName = "Pilot" });
    world.Apply(new PlayerInfo { ShipId = 1, ISide = 0, CharacterName = "Pilot" });
    var ships = new List<Ship>();
    for (int i = 0; i < sectorShips; i++)
    {
        short id = (short)(2 + i);
        var ship = new Ship(id, hull, new SimVec(400 + i * 220, (i % 3) * 90, (i % 5) * 60), SimVec.Zero, Orientation.Identity);
        ship.SetFraction(1f - 0.05f * (i % 10));
        world.Apply(new ExportShip { ShipId = id, HullId = 1, SideId = (short)(i % 2), CharacterName = "Ship" + id });
        ships.Add(ship);
    }
    if (ships.Count > 0) world.Apply(HeavyShipsUpdate.FromShips(101, SimVec.Zero, ships.ToArray()));
    var far = new List<Ship>();
    for (int i = 0; i < offSectorShips; i++)
    {
        short id = (short)(200 + i);
        world.Apply(new ExportShip { ShipId = id, HullId = 1, SideId = 1, CharacterName = "Far" + id });
        far.Add(new Ship(id, hull, new SimVec(5000 + i * 100, 0, 0), SimVec.Zero, Orientation.Identity));
    }
    if (far.Count > 0)
    {
        // HEAVY belongs to the viewer's sector, so park the off-sector ships in a
        // strategic SHIP_STATUS sector instead — the "known but not in view" set.
        foreach (var id in far.Select(s => s.Id))
            world.Apply(new ShipStatusMessage { ShipId = id, HullId = 1, SectorId = 9, StationId = -1, ParentId = -1, StateTime = 200 });
    }
    world.Apply(new ExportStation
    {
        Station = new DataStationIGC([-600f, 0f, 0f], [0f, 1f, 0f], [0f, 0f, 1f], [0f, 0f, 0f, 1f],
            ClusterId: 1, SideId: 0, StationId: 4, StationTypeId: 1, BpHull: 240, BpShield: 240, Name: "Garrison"),
    });
    world.Apply(new ExportWarp
    {
        Warp = new DataWarpIGC(WarpId: 5, DestinationId: 6, Radius: 100, TextureName: "aleph", IconName: "aleph",
            Name: "Aleph", Position: [0f, 0f, 900f], Forward: [0f, 0f, 1f], Rotation: [0f, 0f, 0f, 1f],
            Signature: 1f, ClusterId: 1),
    });
    double now = Environment.TickCount64 / 1000.0;
    for (int i = 0; i < 3; i++)
    {
        world.NowSeconds = () => now - i * 4;
        world.Apply(new ExplosionEvent
        {
            Type = ExplosionType.SmallShip, ClusterId = 1,
            Position = new SimVec(300 + i * 400, 0, 0), Velocity = SimVec.Zero,
        });
    }
    return world;
}

object Scene(int sectorShips, int offSectorShips, int passes)
{
    var world = BattleWorld(sectorShips, offSectorShips);
    var watch = new DirectorWatch(1067);
    watch.Start(1067);
    var position = new Vector3(0, 0, 0);
    // Warm-up (JIT + list growth), then measure.
    for (int i = 0; i < 2_000; i++) watch.Update(1.0 / 60.0, watch.BuildSnapshot(world, position), 12f, 24f, 16f / 9f);
    long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    for (int i = 0; i < passes; i++) watch.Update(1.0 / 60.0, watch.BuildSnapshot(world, position), 12f, 24f, 16f / 9f);
    double microseconds = Stopwatch.GetElapsedTime(start).TotalMicroseconds / passes;
    long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
    // Split the seam: the browser snapshot alone, then the shared desktop planner alone.
    long beforeSnapshot = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < passes; i++) watch.BuildSnapshot(world, position);
    long snapshotBytes = GC.GetAllocatedBytesForCurrentThread() - beforeSnapshot;
    var held = watch.BuildSnapshot(world, position);
    long beforePlanner = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < passes; i++) watch.Update(1.0 / 60.0, held, 12f, 24f, 16f / 9f);
    long plannerBytes = GC.GetAllocatedBytesForCurrentThread() - beforePlanner;
    var snapshot = watch.BuildSnapshot(world, position);
    return new
    {
        sectorShips = sectorShips, offSectorShips = offSectorShips, passes,
        microsecondsPerFrame = Math.Round(microseconds, 3),
        allocatedBytesPerFrame = Math.Round((double)bytes / passes, 2),
        allocatedBytesPerSnapshot = Math.Round((double)snapshotBytes / passes, 2),
        allocatedBytesPerPlannerUpdate = Math.Round((double)plannerBytes / passes, 2),
        subjects = snapshot.Subjects.Count,
        subjectKinds = snapshot.Subjects.GroupBy(s => s.Category.ToString()).ToDictionary(g => g.Key, g => g.Count()),
    };
}

// A 120-second, 60 Hz simulated watch over a 10 v 10 sized battle: how many
// shots the director cuts, what it frames, and whether the seed replays exactly.
(string Signature, int ShotChanges, Dictionary<string, int> Kinds, Dictionary<string, int> Framed) Simulate(int seed)
{
    var world = BattleWorld(20, 0);
    var watch = new DirectorWatch(seed);
    watch.Start(seed);
    var position = new Vector3(0, 0, 0);
    var signature = new StringBuilder();
    var kinds = new Dictionary<string, int>();
    var framed = new Dictionary<string, int>();
    for (int frame = 0; frame < 7_200; frame++)
    {
        var snapshot = watch.BuildSnapshot(world, position);
        watch.Update(1.0 / 60.0, snapshot, 12f, 24f, 16f / 9f);
        signature.Append((int)watch.Planner.Kind).Append(':').Append(watch.Planner.SubjectIndex).Append(';');
        kinds[watch.Planner.Kind.ToString()] = kinds.GetValueOrDefault(watch.Planner.Kind.ToString()) + 1;
        if (watch.Planner.SubjectIndex >= 0 && watch.Planner.SubjectIndex < snapshot.Subjects.Count)
        {
            string category = snapshot.Subjects[watch.Planner.SubjectIndex].Category.ToString();
            framed[category] = framed.GetValueOrDefault(category) + 1;
        }
    }
    string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    return (hash, watch.Planner.ShotChanges, kinds, framed);
}

var a = Simulate(1067);
var b = Simulate(1067);
var report = new
{
    note = "Managed Release JIT harness of the actual browser snapshot/camera seam; excludes projection, drawing, HUD and GPU work. Not browser frame times.",
    seed = 1067,
    determinism = new { first = a.Signature, second = b.Signature, identical = a.Signature == b.Signature },
    shotCadence = new
    {
        simulatedSeconds = 120, frames = 7_200, shotChanges = a.ShotChanges,
        secondsPerShot = Math.Round(120.0 / a.ShotChanges, 2), framesPerKind = a.Kinds, framesPerFramedSubject = a.Framed,
    },
    scenes = new[] { Scene(2, 0, 20_000), Scene(10, 0, 20_000), Scene(20, 0, 20_000), Scene(20, 500, 20_000) },
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("P1067_WATCH_PROOF " + output);
return 0;
