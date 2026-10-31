using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Bot = Allegiance.Bot.Bot;

// Real TCP loopback, separate server process so process-wide allocation includes
// server workers but excludes the 200 clients. No remote hosts or synthetic load.
// Usage: p906-tick-order-proof off|phases|collisions|broadcast output-dir [seconds=20]
string arm = args[0];
if (arm is not ("off" or "phases" or "collisions" or "broadcast")) throw new ArgumentException("unknown arm");
string state = Path.GetFullPath(args[1]);
int seconds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 20;
if (seconds is < 5 or > 120) throw new ArgumentException("bounded proof: 5..120 seconds");
Directory.CreateDirectory(state);
if (args.Contains("--trace")) { Trace(); return; }
if (args.Contains("--host")) { Host(); return; }
foreach (string name in new[] { "port", "ready", "stop", "result.json" }) File.Delete(Path.Combine(state, name));
var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
foreach (string arg in new[] { arm, state, seconds.ToString(CultureInfo.InvariantCulture), "--host" }) start.ArgumentList.Add(arg);
// Pin environmental overrides too, including the already-existing broadcast flag.
start.Environment["ALLEGIANCE_SIM_NATIVE_GOTOPLAN"] = "1";
start.Environment["ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER"] = arm == "broadcast" ? "1" : "0";
start.Environment["ALLEGIANCE_TRANSPORT_MODE"] = "pool";
start.Environment["ALLEGIANCE_TRANSPORT_MAX_PER_IP"] = "256";
using var host = Process.Start(start) ?? throw new IOException("server process failed to start");
var clients = new List<Bot>();
try
{
    Wait(() => File.Exists(Path.Combine(state, "port")), 30);
    ushort port = ushort.Parse(File.ReadAllText(Path.Combine(state, "port")), CultureInfo.InvariantCulture);
    for (int i = 0; i < 200; i++)
    {
        var client = new Bot { AutoSelectSideAfterLogon = false }; clients.Add(client);
        client.Connect(port, $"P906-{i:D3}");
        client.SendLogonRequest(new() { CharacterName = $"P906-{i:D3}", FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue });
        client.SendPositionReq(0); // match the spawner side; avoid native home-sector reconciliation
        if (i % 20 == 19) Wait(() => clients.All(c => c.ShipAssignment is not null), 30);
    }
    Wait(() => clients.All(c => c.ShipAssignment is not null), 30);
    File.WriteAllText(Path.Combine(state, "ready"), "200 seated");
    // The range holds its firing controls on the server. Clients observe
    // through real sockets and retain normal keepalives; reactive pose echoes
    // would otherwise make input timing an uncontrolled A/B variable.
    while (!host.HasExited) Thread.Sleep(100);
    if (host.ExitCode != 0) throw new IOException("server failed; see host.log");
    Console.WriteLine(File.ReadAllText(Path.Combine(state, "result.json")));
}
finally
{
    File.WriteAllText(Path.Combine(state, "stop"), "stop");
    foreach (var client in clients) client.Dispose();
    if (!host.HasExited && !host.WaitForExit(10000)) host.Kill(entireProcessTree: true);
}

void Wait(Func<bool> done, int timeout)
{
    var timer = Stopwatch.StartNew();
    while (!done())
    {
        if (host.HasExited || timer.Elapsed.TotalSeconds > timeout) throw new TimeoutException("loopback seating failed; see host.log");
        Thread.Sleep(10);
    }
}

void Host()
{
    using var log = new StreamWriter(Path.Combine(state, "host.log")) { AutoFlush = true };
    Console.SetOut(log);
    var sim = new SimConfig { NativeGotoPlan = true, NativeBroadcastOrder = arm == "broadcast", CrowdingPenalty = "off" };
    // Reflection deliberately permits the identical proof to build on the
    // dispatch base, where the two new experimental flags do not exist yet.
    typeof(SimConfig).GetProperty("NativeShipPhases")?.SetValue(sim, arm == "phases");
    typeof(SimConfig).GetProperty("NativeCollisionQueue")?.SetValue(sim, arm == "collisions");
    using var server = new Server { SpawnInitialMinersAtBoot = false };
    Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawn = (core, mission, id) =>
    {
        var hull = core.HullTypes.Values.Where(h => h.HasCapability(DataHullTypeIGC.HabmFighter) && h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.MaxFixedWeapons > 0
            && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed && h.Speed > 0).OrderBy(h => h.HullId).First();
        // Durable targets keep all 200 real clients in combat throughout the
        // short sample; use spherical fallback bodies for an unambiguous firing
        // line, independent of the real mesh's narrow hardpoint silhouette.
        // No respawn or station-bound scoring is timed here.
        var ship = new Ship(id, hull with { HitPoints = hull.HitPoints * 100, ModelName = "" }, new(5000 + id % 20 * 200, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return (ship, mission.Sectors[0]);
    };
    typeof(Server).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, spawn);
    server.Boot(new()
    {
        ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
        UpdatesPerSecond = 20, BenchmarkSeed = 906, Sim = sim,
        Transport = new() { Mode = "pool", PoolWorkers = 4, MaxConnectionsPerIp = 256 },
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true,
            MaxDegreeOfParallelism = 4, ShipUpdateModel = "native", InterestTiers = false, MinUpdateHz = 2 },
        Receive = new() { Decouple = false },
        LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
        Games = [new() { Name = "P906 local 100v100", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
            SideNames = ["Alpha", "Bravo"], ShowMap = true, MaxPlayersPerTeam = 200, MaxTeamImbalance = 0x7fff }]
    });
    var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
    PrepareArena(mission);
    File.WriteAllText(Path.Combine(state, "port"), server.BoundPort.ToString(CultureInfo.InvariantCulture));
    var deadline = Stopwatch.StartNew();
    while (!File.Exists(Path.Combine(state, "ready")))
    {
        if (deadline.Elapsed.TotalSeconds > 90 || File.Exists(Path.Combine(state, "stop"))) return;
        server.TickSim(); Thread.Sleep(10);
    }
    var ships = mission.GetShips().OrderBy(s => s.Id).ToArray();
    if (ships.Length != 200) throw new IOException($"expected 200 placed ships, got {ships.Length}");
    var weapon = mission.Core.PartTypes.Values.Select(p => p.Type).OfType<DataPartTypeIGC.Weapon>()
        .Where(w => w.Dispersion == 0 && w.DtimeBurst > 0
            && mission.Core.ProjectileTypes[w.ProjectileTypeId] is { Power: > 0 } pt
            && pt.Speed * pt.Lifespan > 2000
            && mission.Core.Constants.DamageConstants[pt.DamageType & 31][ships[0].HullType.DefenseType] > 0)
        .OrderBy(w => w.EnergyPerShot).First();
    for (int i = 0; i < ships.Length; i++)
    {
        var ship = ships[i]; int lane = i / 2;
        ship.ChangeHull(ship.HullType with { ModelName = "", Length = 100 });
        ship.Hull = null;
        ship.SideId = (short)(i % 2);
        ship.SetFraction(1);
        ship.Position = new(5000 + lane % 10 * 250, 5000 + lane / 10 * 250, i % 2 == 0 ? 5000 : 4820);
        ship.Velocity = Vector3.Zero;
        ship.SetOrientation(Orientation.Identity.Copy());
        if (i % 2 != 0) ship.Orientation.Yaw(MathF.PI);
        ship.MountWeapon(weapon); ship.Energy = ship.HullType.MaxEnergy;
        ship.Controls = new(0, 0, 0, -1);
        ship.StateM = ShipControlState.OneWeapon | ShipControlState.CoastButton;
    }
    var projectile = mission.Core.ProjectileTypes[weapon.ProjectileTypeId];
    Console.WriteLine($"[proof] hull={ships[0].HullType.HullId} weaponProjectile={projectile.ProjectileTypeId} speed={projectile.Speed} lifespan={projectile.Lifespan} power={projectile.Power}");
    mission.SeedStartingVisibility();
    var ticks = new List<double>(); var allocations = new List<long>();
    // Ten seconds of real battle warm-up, then the requested timed window.
    long shotStart = 0, hitStart = 0, killStart = 0;
    for (int tick = 0; tick < 200 + seconds * 20 && !File.Exists(Path.Combine(state, "stop")); tick++)
    {
        if (tick == 200)
        { shotStart = Count(ShowcaseSkill.Guns); hitStart = Count(ShowcaseSkill.ShipDamage); killStart = Count(ShowcaseSkill.ShipKills); }
        // Bounded firing-range fixture: refill outside the timed tick so the
        // whole sample exercises real weapon/collision traffic, not dry guns.
        foreach (var ship in ships) { ship.Ammo = ship.HullType.MaxAmmo; ship.Energy = ship.HullType.MaxEnergy; }
        long allocated = GC.GetTotalAllocatedBytes(precise: false);
        long at = Stopwatch.GetTimestamp();
        server.TickSim();
        double ms = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
        long bytes = GC.GetTotalAllocatedBytes(precise: false) - allocated;
        if (tick >= 200) { ticks.Add(ms); allocations.Add(bytes); }
        int pause = (int)Math.Max(0, 50 - ms);
        if (pause > 0) Thread.Sleep(pause);
    }
    ticks.Sort(); allocations.Sort();
    if (ticks.Count == 0) throw new IOException("measurement was interrupted");
    var result = new Measurement(arm, 906, ships.Length, ticks.Count, ticks.Average(), ticks[(int)(ticks.Count * .95)],
        allocations.Average(), allocations[(int)(allocations.Count * .95)], Count(ShowcaseSkill.Guns) - shotStart,
        Count(ShowcaseSkill.ShipDamage) - hitStart, Count(ShowcaseSkill.ShipKills) - killStart,
        ships.Count(s => s.Dead), ships.Sum(s => (double)s.Fraction), mission.GetShips().Count, server.SimPassStageFailures,
        Enumerable.Range(0, 100).Count(i => ships[i * 2].SideId != ships[i * 2 + 1].SideId),
        ships.Count(s => ReferenceEquals(s.Sector, mission.Sectors[0]) && s.StationId is null));
    File.WriteAllText(Path.Combine(state, "result.json"), JsonSerializer.Serialize(result, ProofJson.Default.Measurement) + "\n");
    if (result.ArenaShips != 200) throw new IOException("ships left the firing range");
    if (result.OpposedPairs != 100) throw new IOException("firing-range sides changed");
    if (result.SimPassStageFailures != 0) throw new IOException("simulation stage failed");
    if (result.Shots == 0 || result.DamageEvents == 0) throw new IOException("combat sample was idle");
    long Count(ShowcaseSkill skill) => mission.Sides.Sum(s => mission.Skills!.Count(s.Id, skill));
}

// File-map top-up uses an unseeded native-style random source. Pin the arena
// itself so the A/B compares the ordering switches against identical geometry.
void PrepareArena(Mission mission)
{
    var sector = mission.Sectors[0];
    foreach (var warp in sector.Warps.ToArray()) sector.RemoveWarp(warp);
    foreach (var station in sector.StationRuntimes.ToArray()) sector.RemoveStation(station);
    foreach (var rock in sector.Mineable.ToArray()) sector.RemoveAsteroid(rock);
    foreach (var treasure in sector.Models.OfType<Treasure>().ToArray()) sector.Terminate(treasure);
    mission.TreasureParameters.PlayerSectorTreasureRate = 0;
    mission.TreasureParameters.NeutralSectorTreasureRate = 0;
    for (short i = 0; i < 24; i++)
        mission.PlaceAsteroid(new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        {
            AsteroidId = (short)(25000 + i), ClusterId = sector.Id, Radius = 50, HitPoints = 10000,
            Position = [1000 + i % 8 * 200, 1000 + i / 8 * 200, -3000]
        }), sector);
}

// A fixed-input production-Mission replay complements the socket timings: real
// client ingress schedules cannot provide a bitwise equivalence assertion.
void Trace()
{
    if (arm != "off") throw new ArgumentException("trace certifies the unchanged off path");
    using var log = new StreamWriter(Path.Combine(state, "trace.log"));
    Console.SetOut(log);
    using var server = new Server { SpawnInitialMinersAtBoot = false };
    server.Boot(new()
    {
        ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
        UpdatesPerSecond = 20, BenchmarkSeed = 906, Sim = new() { NativeGotoPlan = true, CrowdingPenalty = "off" },
        Transport = new() { Mode = "pool" },
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
        LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
        Games = [new() { Name = "P906 fixed-input replay", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }]
    });
    var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
    PrepareArena(mission);
    var sector = mission.Sectors[0];
    var hull = mission.Core.HullTypes.Values.First(h => h.Speed > 0 && h.HitPoints > 0) with { HitPoints = 10000 };
    var ships = new Ship[200];
    for (int i = 0; i < ships.Length; i++)
    {
        var ship = new Ship((short)(10000 + i), hull, new(1000 + i % 20 * 100, 1000 + i / 20 * 100, 1000),
            new(i % 3 - 1, 0, -10), new Orientation()) { SideId = (short)(i % 2) };
        mission.PlaceShip(ship, sector); ships[i] = ship;
    }
    var projectile = mission.Core.ProjectileTypes.Values.First(p => p.Power > 0) with { Power = 1, Lifespan = 10 };
    using var trace = new MemoryStream();
    using var writer = new BinaryWriter(trace);
    int rounds = 0;
    for (int tick = 0; tick < seconds * 20; tick++)
    {
        for (int i = 0; i < ships.Length; i++)
        {
            ships[i].Controls = new((i % 3 - 1) * .1f, 0, 0, -.75f);
            ships[i].StateM = tick % 20 < 10 ? ShipControlState.CoastButton : 0;
            if (tick % 40 == 0 && i % 2 == 0)
            {
                sector.Add(new Projectile(mission.NextObjectId(), sector, projectile,
                    ships[i + 1].Position, new(0, 0, -100), new(0, 0, -1), mission.LastUpdate, ships[i]));
                rounds++;
            }
        }
        mission.Update((tick + 1) / 20d);
        foreach (var ship in ships)
        {
            writer.Write(ship.Id); Write(ship.Position); Write(ship.Velocity); Write(ship.Orientation.GetForward());
            writer.Write(ship.Fraction); writer.Write(ship.StateM); writer.Write(ship.Energy); writer.Write(ship.Ammo);
            writer.Write(ship.Dead); writer.Write(ship.StationId ?? (short)-1);
        }
        writer.Write(sector.Models.Count);
    }
    writer.Flush();
    if (args.Contains("--trace-bytes")) File.WriteAllBytes(Path.Combine(state, "trace.bin"), trace.ToArray());
    var result = new TraceMeasurement(906, seconds * 20, ships.Length, rounds, trace.Length,
        Convert.ToHexString(SHA256.HashData(trace.GetBuffer().AsSpan(0, (int)trace.Length))));
    File.WriteAllText(Path.Combine(state, "trace-result.json"), JsonSerializer.Serialize(result, ProofJson.Default.TraceMeasurement) + "\n");
    void Write(Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
}

internal sealed record TraceMeasurement(int Seed, int Ticks, int Ships, int Rounds, long TraceBytes, string Sha256);

internal sealed record Measurement(string Arm, int Seed, int Clients, int Ticks, double TickMeanMs, double TickP95Ms,
    double AllocationMeanBytes, long AllocationP95Bytes, long Shots, long DamageEvents, long Kills,
    int DeadInitialShips, double FinalHullFractionSum, int LiveMissionShips, long SimPassStageFailures, int OpposedPairs, int ArenaShips);
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Measurement))]
[JsonSerializable(typeof(TraceMeasurement))]
internal partial class ProofJson : JsonSerializerContext;
