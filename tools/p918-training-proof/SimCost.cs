using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

// Uses only pre-P918 APIs so the same source can run against the dispatch
// assembly in a scratch harness. Real native GotoPlan sim, no artificial load.
internal static class SimCost
{
    private sealed class Site : IIgcSite
    {
        public int Changes;
        public void CommandChangedEvent(short command, IShipIgc ship, IModelIgc? target, short cid) => Changes++;
    }
    public static void Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("sim-cost expects artwork and output JSON");
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(args[0], "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(args[0], "XXV_simple.igc")));
        var durations = new List<double>(); var allocations = new List<long>();
        string? fingerprint = null;
        for (int run = 0; run < 10; run++)
        {
            var site = new Site();
            using var m = Mission.Assemble(core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], site);
            m.Stage = MissionStage.Started;
            m.NativeGotoPlanEnabled = true;
            m.NativeShipPhasesEnabled = m.NativeCollisionQueueEnabled = false;
            var ships = new List<Ship>();
            var hull = core.HullTypes[310];
            var orientation = Orientation.Identity; orientation.Set(new(1, 0, 0), new(0, 0, 1));
            for (short id = 0; id < 32; id++)
            {
                var ship = new Ship((short)(30000 + id), hull, new(-6000, -4000 + id * 200, 2000), Vector3.Zero, orientation)
                { SideId = 0, PilotType = Ship.PilotTypeWingman, IsNpc = true, Autopilot = true };
                m.PlaceShip(ship, m.Sectors[0]);
                ship.SetGoto(id < 16
                    ? new(GotoKind.Waypoint, m.Sectors[0].Id, new(8000, -4000 + id * 200, 2000))
                    : new(GotoKind.Waypoint, m.Sectors[0].Id, new(-6000, -4000 + ((id + 1) % 32) * 200, 2000),
                        TargetType: IgcObjectType.Ship, TargetId: (short)(30000 + (id + 1) % 32)));
                ships.Add(ship);
            }
            long startAlloc = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (int tick = 1; tick <= 250; tick++)
            {
                if (tick % 10 == 0)
                    for (int i = 0; i < ships.Count; i++)
                    {
                        ships[i].SetCommand(OrderChange.CmdCurrent, ships[(i + tick / 10) % ships.Count], OrderChange.CidDefend);
                        ships[i].SetCommand(OrderChange.CmdAccepted, ships[(i + 1) % ships.Count], OrderChange.CidDefend);
                    }
                m.Update(tick * .05);
            }
            timer.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - startAlloc;
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                foreach (var ship in ships)
                {
                    writer.Write(ship.Id); writer.Write(ship.Position.X); writer.Write(ship.Position.Y); writer.Write(ship.Position.Z);
                    writer.Write(ship.Velocity.X); writer.Write(ship.Velocity.Y); writer.Write(ship.Velocity.Z);
                    writer.Write(ship.Fraction); writer.Write(ship.CurrentCommandId); writer.Write(ship.AcceptedShipCommandId);
                    writer.Write(ship.CommandTarget?.Id ?? -1); writer.Write(ship.AcceptedShipTarget?.Id ?? -1);
                }
                writer.Write(site.Changes); writer.Write(m.GetBuoys().Count);
            }
            string hash = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
            if (fingerprint is not null && hash != fingerprint) throw new InvalidOperationException("Nondeterministic multiplayer simulation trace.");
            fingerprint = hash;
            if (run == 0) continue; // JIT and cache warmup
            durations.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(allocated);
        }
        var result = new JsonObject
        {
            ["scenario"] = "32 multiplayer wingmen with point and typed ship goals, 250 native GotoPlan ticks, current/accepted replacements every ten ticks",
            ["nativeGotoPlan"] = true, ["nativeShipPhases"] = false, ["nativeCollisionQueue"] = false,
            ["runs"] = durations.Count, ["ticksPerRun"] = 250, ["fingerprint"] = fingerprint,
            ["milliseconds"] = new JsonArray(durations.Select(d => JsonValue.Create(d)).ToArray()),
            ["allocatedBytes"] = new JsonArray(allocations.Select(a => JsonValue.Create(a)).ToArray()),
        };
        File.WriteAllText(args[1], result.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine($"P918_SIM_COST {fingerprint} median={durations.Order().ElementAt(durations.Count / 2):F3}ms alloc={allocations.Min()}..{allocations.Max()}");
    }
}
