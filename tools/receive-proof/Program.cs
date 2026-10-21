using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Bot = Allegiance.Bot.Bot;
using Srv = Allegiance.Server.Server;

// Real TCP crowded-loopback A/B. No remote hosts. Ordinary cadence and idle
// roster windows are followed by explicitly synthetic CRC-heavy burst windows.
// Usage: dotnet run --project tools/receive-proof -- <off|on> <output-dir> [peers=390]
bool decouple = args[0] == "on";
string output = Path.GetFullPath(args[1]);
int count = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 390;
Directory.CreateDirectory(output);
using var transcript = new StreamWriter(Path.Combine(output, "server.log")) { AutoFlush = true };
Console.SetOut(TextWriter.Synchronized(transcript));
var observations = new List<object>();
using var server = new Srv { SpawnInitialMinersAtBoot = false };
int placed = 0;
server.ShipSpawner = (_, mission, id) =>
{
    var sector = mission.Sectors[0];
    var hull = mission.Core.HullTypes.Values.First(h => h.Speed > 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
    // Keep every player in one sector but avoid incidental collisions/docking.
    int n = placed++;
    var ship = new Ship(id, hull, new Vector3(10000 + n % 20 * 120, 10000 + n / 20 * 120, 5000), Vector3.Zero, Orientation.Identity)
    {
        SideId = mission.Sides[n % mission.Sides.Count].Id,
    };
    mission.PlaceShip(ship, sector);
    return (ship, sector);
};
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
    StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
    Receive = new ReceiveConfig { Decouple = decouple, Measure = true },
    Transport = new TransportConfig { Mode = "pool", PoolWorkers = 4, MaxConnectionsPerIp = 1024, DataFrameReadTimeoutSeconds = 120 },
    Broadcast = new BroadcastConfig { ParallelBuild = true, MaxDegreeOfParallelism = 4, ShipUpdateModel = "native" },
    Profiler = new ProfilerConfig { Enabled = true, SlowTickCapture = true, SlowTickMs = 50, CsvPath = Path.Combine(output, "tick.csv") },
    LiveSettings = new LiveSettingsConfig { AutoNextMatch = new AutoNextMatchConfig { Enabled = false } },
    Games = [new GameConfig { Name = "P673 local", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Beta"] }],
});
var host = (ServerHost)typeof(Srv).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
var bots = new List<Bot>();
var senders = new List<Action<byte[]>>();
try
{
    for (int i = 0; i < count; i++)
    {
        var bot = new Bot();
        bot.Connect(server.BoundPort, $"p673-{i}");
        bot.SendLogonRequest(new LogonRequest { CharacterName = $"p673-{i}", FedsrvVer = LogonRequest.MsgVer });
        bots.Add(bot);
        senders.Add(typeof(Bot).GetMethod("Send", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<byte[]>>(bot));
        // Seat in small cohorts to isolate steady-state traffic from join bursts.
        if ((i + 1) % 20 == 0 || i + 1 == count)
            PumpUntil(() => bots.All(b => b.ShipAssignment is not null), 60);
    }
    server.Missions[0].Stage = MissionStage.Started;
    for (int i = 0; i < 10; i++) Tick();
    // No application traffic: prove periodic traffic does not elicit bot replies.
    long idleStart = host.PacketsReceived;
    long idleTick = server.LoopIterations;
    Console.WriteLine($"[receive-proof] phase=idle-rosters startTick={idleTick}");
    for (int i = 0; i < 100; i++) PacedTick();
    // P774 — the port-only score-roster snapshot (209) and patch (219) are
    // retired; the roster is derived from PLAYERINFO(10) seeds + KILL_SHIP(105).
    // The crowded-proof precondition is therefore: every peer holds a
    // PLAYERINFO for every mission ship (HUD-20, FedSrv.CPP:2129-2134).
    if (bots.Any(b => b.PlayerInfos.Select(p => p.ShipId).Distinct().Count() < count))
        throw new Exception("requires PLAYERINFO for every mission ship");
    observations.Add(new { phase = "idle-rosters", startTick = idleTick, endTick = server.LoopIterations,
        packets = host.PacketsReceived - idleStart,
        playerInfosPerPeer = bots.Select(b => b.PlayerInfos.Count).ToArray(),
        distinctPlayerInfoShips = bots[0].PlayerInfos.Select(p => p.ShipId).Distinct().Count() });

    // Match the bot manager's aligned decision sends, here at client 150ms cadence.
    var control = FedPacket.Pack(new ShipUpdate { Payload = ClientShipUpdate.FromControls(0, 0, new ControlData(.1f, 0, 0, 0)) }.Encode());
    Console.WriteLine($"[receive-proof] phase=ordinary-controls startTick={server.LoopIterations}");
    for (int tick = 0; tick < 100; tick++)
    {
        if (tick % 3 == 0) SendAligned(control, 1);
        PacedTick();
    }

    // The codec already accepts trailing bytes on a fixed-size SHIP_UPDATE.
    // Padding keeps command count/state work identical while loading CRC/framing:
    // 65,532 message bytes + 4 CRC = the existing 64KiB inbound frame ceiling.
    // This is a synthetic capacity test, not a claimed normal client payload.
    var padded = new byte[65532];
    FedPacket.Unpack(control)[0].CopyTo(padded, 0);
    BinaryPrimitives.WriteUInt16LittleEndian(padded, (ushort)padded.Length);
    var heavy = FedPacket.Pack(padded);
    Console.WriteLine($"[receive-proof] phase=synthetic-padded-bursts startTick={server.LoopIterations}");
    for (int burst = 0; burst < 5; burst++)
    {
        SendAligned(heavy, 3);
        Tick();
        for (int i = 0; i < 4; i++) Tick();
    }
    Console.WriteLine($"[receive-proof] phase=settle startTick={server.LoopIterations}");
    int remaining = (int)((100 - server.LoopIterations % 100) % 100);
    for (int i = 0; i < remaining; i++) PacedTick(); // retain the final complete histogram window
    if (server.SimPassStageFailures != 0) throw new Exception($"stage failures: {server.SimPassStageFailures}");
    observations.Add(new { phase = "final", peers = server.Ships.Count, stageFailures = server.SimPassStageFailures, packets = host.PacketsReceived, drained = host.EventsDrained, deferred = host.EventsDeferred });
    File.WriteAllText(Path.Combine(output, "observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    foreach (var bot in bots) bot.Dispose();
    server.Shutdown();
}
return;

void Tick() => server.TickSim();
void PacedTick()
{
    var timer = Stopwatch.StartNew();
    Tick();
    int sleep = (int)Math.Max(0, 50 - timer.Elapsed.TotalMilliseconds);
    if (sleep > 0) Thread.Sleep(sleep);
}
void PumpUntil(Func<bool> done, int seconds)
{
    var deadline = Stopwatch.StartNew();
    while (!done())
    {
        if (deadline.Elapsed.TotalSeconds > seconds) throw new TimeoutException("loopback seating timed out");
        Tick();
        Thread.Sleep(5);
    }
}
void SendAligned(byte[] packet, int perPeer)
{
    long before = host.PacketsReceived;
    long expected = (long)count * perPeer;
    var deadline = Stopwatch.StartNew();
    for (int n = 0; n < perPeer; n++)
        foreach (var send in senders) send(packet);
    // Hold the next simulation boundary until every real socket reader published
    // the same complete burst. This deliberately isolates drain cost from transit.
    while (host.PacketsReceived - before < expected)
    {
        if (deadline.Elapsed.TotalSeconds > 60) throw new TimeoutException($"burst receive {host.PacketsReceived - before}/{expected}");
        Thread.Sleep(1);
    }
    observations.Add(new { phase = packet.Length > 1000 ? "synthetic-padded-burst" : "ordinary-control-burst", tick = server.LoopIterations, bytes = packet.Length, packets = expected, receiveWallMs = deadline.Elapsed.TotalMilliseconds });
}
