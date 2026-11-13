using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Srv = Allegiance.Server.Server;

// Production menu host, research/Normal/Small defaults, ordinary side-zero pilot.
// Observers retain the real server handler. No grants, visibility edits, shortened
// countdown, speedup or bot-policy overrides. --seed pins the host's supported seed;
// concurrent bot scheduling is still nondeterministic. Run one match per process.
string Option(string name, string fallback) => Array.IndexOf(args, name) is var i && i >= 0 ? args[i + 1] : fallback;
var output = Path.GetFullPath(Option("--output", ".scratch/p1007/run"));
Directory.CreateDirectory(output);
int seed = int.Parse(Option("--seed", "1007"), CultureInfo.InvariantCulture);
var size = Enum.Parse<SkirmishSize>(Option("--size", "Small"), true);
double seconds = double.Parse(Option("--seconds", "600"), CultureInfo.InvariantCulture);
if (!double.IsFinite(seconds) || seconds < 1 || seconds > 600) throw new ArgumentException("--seconds must be 1..600");
var progress = Console.Out;
using var log = new StreamWriter(Path.Combine(output, "server.log")) { AutoFlush = true };
using var events = new StreamWriter(Path.Combine(output, "events.jsonl")) { AutoFlush = true };
Console.SetOut(TextWriter.Synchronized(log));
var options = new SkirmishOptions { Seed = seed, Size = size, StartAsSpectator = false };
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
SkirmishHost? host = null;
try
{
    host = await SkirmishHost.StartAsync(options, driverPath: Option("--driver", SkirmishHost.FindDriver()), cancellationToken: deadline.Token);
    const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
    var server = (Srv)typeof(SkirmishHost).GetField("_server", fields)!.GetValue(host)!;
    var simGate = typeof(SkirmishHost).GetField("_simGate", fields)!.GetValue(host)!;
    var mission = server.Missions[0];
    double? started = null;
    int alerts = 0, hitCount = 0;
    void Record(object row) { lock (events) events.WriteLine(JsonSerializer.Serialize(row)); }
    // Before/after bracket identifies exactly which callback passed the production
    // throttle. Read health next sample, explicitly end-of-tick rather than per-hit.
    double stampBefore = 0;
    lock (simGate)
    {
        var site = mission.Site;
        var hook = site.GetType().GetField("StationDamaged")!;
        Action<Station, Model?, byte, float> before = (st, _, _, _) => stampBefore = st.LastDamageReport;
        Action<Station, Model?, byte, float> after = (st, launcher, type, amount) =>
        {
            hitCount++;
            var ship = launcher as Ship;
            bool report = st.LastDamageReport != stampBefore && ship?.SideId != st.SideId;
            if (report) alerts++;
            Record(new { kind = report ? "alert" : "damage-callback", sim = server.SimNow,
                matchSeconds = started is { } start ? server.SimNow - start : (double?)null,
                station = st.StationId, stationName = st.Data.Name, stationSide = st.SideId,
                sector = st.ClusterId, attacker = ship?.Id, attackerName = ship?.Name,
                attackerSide = ship?.SideId, pilotType = ship?.PilotType,
                hull = ship?.HullType.HullId, hullName = ship?.HullType.Name,
                damageType = type, amount,
                matrixShield = mission.Core.Constants.DamageConstants[type & 0x1f][st.Type.DefenseTypeShield],
                matrixArmor = mission.Core.Constants.DamageConstants[type & 0x1f][st.Type.DefenseTypeArmor],
                shieldBefore = st.ShieldFraction, armorBefore = st.ArmorFraction,
                reportGap = double.IsFinite(stampBefore) ? server.SimNow - stampBefore : (double?)null,
                friendly = ship?.SideId == st.SideId, mission.AllowFriendlyFire });
        };
        hook.SetValue(site, Delegate.Combine(before, (Delegate?)hook.GetValue(site), after));
        Record(new { kind = "config", seed, size = size.ToString(), options.Map, options.TechMode,
            speed = options.Speed.ToString(), options.TeamSize, options.StartAsSpectator,
            showMap = options.CreateConfig().Games[0].ShowMap, options.AllowFriendlyFire,
            fog = "native object visibility; default Small/Medium reveal warp topology only",
            initialSim = server.SimNow, pids = host.BotProcessIds,
            stations = mission.Sectors.SelectMany(s => s.StationRuntimes).Select(st => new { st.StationId, st.SideId, st.Type.DefenseTypeShield,
                st.Type.DefenseTypeArmor, st.Type.MaxShieldHitPoints,
                shieldMatrix = mission.Core.Constants.DamageConstants.Select(row => row[st.Type.DefenseTypeShield]).ToArray(),
                armorMatrix = mission.Core.Constants.DamageConstants.Select(row => row[st.Type.DefenseTypeArmor]).ToArray() }).ToArray() });
    }
    using var pilot = new ConnectionSession();
    int chatCount = 0, voices = 0;
    long chatSequence = 0;
    pilot.World.ChatReceived += () =>
    {
        foreach (var chat in pilot.World.Events.ChatsAfter(chatSequence))
        {
            if (!chat.Message.Contains("under attack by", StringComparison.OrdinalIgnoreCase)) continue;
            if (chat.SidSender == -1) chatCount++;
            Record(new { kind = chat.SidSender == -1 ? "pilot-station-chat" : "pilot-ship-chat",
                sim = host.SimulationSeconds, chat.Message, chat.VoiceOver,
                chat.ChatTarget, chat.OidRecipient, chat.SidSender });
        }
        chatSequence = pilot.World.Events.ChatSequence;
    };
    pilot.Connect(host.Port, "skirmish-test-pilot");
    pilot.SendLogonRequest(new() { CharacterName = "skirmish-test-pilot", FedsrvVer = LogonRequest.MsgVer,
        ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", Time = 1, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 72701 });
    pilot.WaitForAck(10000);
    pilot.SendPositionReq(0);
    bool ready = false;
    double nextLaunch = 0, nextSample = 0;
    var wall = Stopwatch.StartNew();
    while (true)
    {
        deadline.Token.ThrowIfCancellationRequested();
        if (!host.BotsAlive || pilot.Lost) throw new IOException(host.Failure ?? "Pilot connection lost");
        double now;
        lock (simGate)
        {
            now = server.SimNow;
            if (mission.Stage == MissionStage.Started) started ??= now;
            if (started is { } start && now - start >= seconds) break;
            if (mission.Stage == MissionStage.Over) throw new IOException("Match ended before observation window");
            if (now >= nextSample)
            {
                nextSample = now + 30;
                var stats = host.Stats;
                Record(new { kind = "snapshot", sim = now, matchSeconds = started is { } s ? now - s : (double?)null,
                    stage = mission.Stage.ToString(), stats = stats.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : stats,
                    sides = mission.Sides.Select(side => new { side.Id, side.Money, side.Kills, side.Deaths,
                        buckets = side.Buckets.Count(b => b.InvestedMoney > 0) }).ToArray(),
                    stations = mission.Sectors.SelectMany(s => s.StationRuntimes).Select(st => new
                        { st.StationId, st.SideId, st.ShieldFraction, st.ArmorFraction }).ToArray(),
                    ships = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).Select(ship => new
                        { ship.Id, ship.SideId, ship.Name, ship.PilotType, hull = ship.HullType.Name,
                            ship.StationId, sector = ship.Sector?.Id, ship.Dead,
                            position = new[] { ship.Position.X, ship.Position.Y, ship.Position.Z } }).ToArray() });
                progress.WriteLine($"seed={seed} {size} wall={wall.Elapsed.TotalSeconds:F0} sim={now:F1} match={(started is { } st ? now - st : 0):F1} alerts={alerts} pilotChats={chatCount}");
            }
        }
        lock (pilot.Gate)
        {
            if (pilot.World.MyShipId is { } id && pilot.World.Ships.TryGetValue(id, out var ship))
            {
                if (pilot.World.MySideId != 0) throw new IOException("Pilot joined wrong side");
                if (!ready) { pilot.SendPlayerReady(true, id); ready = true; }
                if (ship.StationId >= 0 && started is not null && now >= nextLaunch)
                { pilot.SendBuyLoadout(ship.HullId, true, ship.Loadout); nextLaunch = now + 2; }
                if (ship.Latest is { } pose)
                {
                    var enemy = pilot.World.Ships.Values.Where(s => s.SideId == 1 && !s.IsNotVisible
                        && s.Latest is not null && s.SectorId == ship.SectorId)
                        .OrderBy(s => (s.Latest!.Position - pose.Position).LengthSquared()).FirstOrDefault();
                    var controls = enemy?.Latest is { } target
                        ? AutopilotSteering.Pursue(pose.Position, pose.Orientation, target.Position)
                        : new ControlData(.12f, .04f, 0, 1);
                    pilot.CommandControls(controls, enemy is null ? 0 : ShipControlState.AllWeapons);
                    host.SetFocusSector(ship.SectorId);
                }
            }
            foreach (var sound in pilot.World.TakeSoundEvents())
                if (sound.Kind == Allegiance.ClientCore.State.ClientSoundEventKind.ChatVoiceOver) voices++;
            pilot.SendPing();
        }
        await Task.Delay(50, deadline.Token);
    }
    Record(new { kind = "summary", seed, simulationSeconds = server.SimNow, started, seconds,
        alerts, hitCount, pilotChats = chatCount, voiceEvents = voices, wallSeconds = wall.Elapsed.TotalSeconds,
        host.BotsAlive, pilotSide = pilot.World.MySideId, pids = host.BotProcessIds });
    File.Copy(Path.Combine(host.DirectoryPath, "driver.log"), Path.Combine(output, "driver.log"), true);
    foreach (var workerLog in Directory.EnumerateFiles(host.DirectoryPath, "err.worker-*.log"))
        File.Copy(workerLog, Path.Combine(output, Path.GetFileName(workerLog)), true);
    progress.WriteLine($"Completed seed={seed} alerts={alerts} pilotChats={chatCount}");
}
finally
{
    host?.Dispose();
    Console.SetOut(progress);
}
