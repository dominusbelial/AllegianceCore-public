using System.Diagnostics;
using System.Globalization;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;

// Bounded actual simulation work, no artificial background load. Run the same
// source against the dispatch base and working tree in alternating processes.
Console.WriteLine($"P911 serverGC={System.Runtime.GCSettings.IsServerGC} label={args.FirstOrDefault() ?? "current"}");
using var server = new Server { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    Port = 0, Discovery = new() { Enabled = false },
    Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
    Transport = new() { Mode = "pool", MaxConnections = 32, MaxConnectionsPerIp = 32 },
    Sim = new() { NativeGotoPlan = true },
    Games = [new() { Name = "P911 unbound tick", Map = "m2xspiderw.igc", Teams = 2,
        CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }],
});
var mission = server.Missions[0];
mission.Stage = MissionStage.Started;
var hull = mission.Core.HullTypes[315];
for (short i = 0; i < 30; i++)
{
    var ship = new Ship((short)(7000 + i), hull, new Vector3(i * 50, 0, 300), Vector3.Zero, Orientation.Identity)
    { IsNpc = true, PilotType = Ship.PilotTypeWingman, SideId = (short)(i % 2), Name = "unbound" };
    mission.PlaceShip(ship, mission.Sectors[i % 2]);
}
for (int i = 0; i < 200; i++) server.TickSim();
for (int sample = 0; sample < 5; sample++)
{
    long allocated = GC.GetTotalAllocatedBytes();
    long start = Stopwatch.GetTimestamp();
    for (int i = 0; i < 1000; i++) server.TickSim();
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    allocated = GC.GetTotalAllocatedBytes() - allocated;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"P911 tick sample={sample} ticks=1000 ms={elapsed:F4} bytes={allocated} ships={mission.GetShips().Count}"));
}
