using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Tools.P925;

internal static class Loopback
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(StaticCore core, JsonElement audit, string directory, bool nativeTurn, bool swap, bool strike, string targetHome)
    {
        if (nativeTurn) throw new InvalidOperationException("No runtime fix: isolated replay does not reproduce a turn-controller defect");
        string label = $"{(strike ? "strike" : "pods")}-{(swap ? "swapped" : "normal")}-{(nativeTurn ? "on" : "off")}";
        if (strike) label += "-" + targetHome;
        var progress = Console.Out;
        using var log = TextWriter.Synchronized(new StreamWriter(Path.Combine(directory, label + ".log")) { AutoFlush = true });
        Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(new()
            {
                Port = 0, SideSelectAutoSeatSeconds = 30, BenchmarkSeed = 925,
                Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
                Transport = new() { Mode = "pool" }, Sim = new() { NativeGotoPlan = true },
                LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false },
                    TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true } },
                Games = [new() { Name = "P925", Map = "m2xspiderw.igc", Teams = 2, CivIds = [18, 4],
                    HomeSwap = swap ? "on" : "off", ShowMap = true, MaxTeamImbalance = 0x7fff,
                    SideNames = ["Alpha", "Bravo"] }],
            });
            var mission = server.Missions[0];
            FixtureMap.PinRocks(mission, MissionMap.Load(IgcReader.ParseFile(Path.Combine(
                ExternalPaths.Resolve(ExternalResourceKind.Artwork), "m2xspiderw.igc"))));
            mission.Stage = MissionStage.Started;
            using var ticker = new Ticker(server);
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            if (strike)
            {
                RunStrike(core, server, mission, manager, ticker, directory, label, progress, targetHome);
                return;
            }
            var samples = audit.GetProperty("samples").EnumerateArray()
                .Where(s => s.GetProperty("Match").GetInt32() == (swap ? 2 : 1)).ToArray();
            var outcomes = new List<Trial>();
            foreach (var sample in samples)
            {
                short side = sample.GetProperty("Side").GetInt16();
                var hull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmFighter)
                    && !h.HasCapability(DataHullTypeIGC.HabmLifepod) && h.MaxFixedWeapons > 0);
                var spawn = manager.SpawnBot(hull.HullId, hull.Name, side, [], "combat");
                var bot = ((IEnumerable)typeof(BotManager).GetField("_bots", Hidden)!.GetValue(manager)!).Cast<object>()
                    .Single(b => (short)b.GetType().GetField("ShipId")!.GetValue(b)! == spawn.ShipId);
                PinLoiter(bot, spawn.ShipId);
                var session = (ConnectionSession)bot.GetType().GetField("Cs")!.GetValue(bot)!;
                ticker.Invoke(() =>
                {
                    var ship = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).Single(s => s.Id == spawn.ShipId);
                    var old = ship.Sector!;
                    var home = mission.Sectors.Single(s => s.Id == sample.GetProperty("HomeSector").GetInt16());
                    if (old != home)
                    {
                        old.Remove(ship); mission.PlaceShip(ship, home);
                        typeof(Srv).GetMethod("OnShipWarped", Hidden)!.Invoke(server, [ship, old, home]);
                    }
                    // Actual server ejection first, then restore the sampled end
                    // pose with a fresh oxygen budget. No direct ChangeHull shortcut.
                    ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, Vector3.Zero, null);
                    if (ship.HullType.HullId != sample.GetProperty("PodHull").GetInt16()) throw new InvalidOperationException("wrong pod");
                    ship.Position = V(sample, "EndPosition"); ship.Velocity = V(sample, "EndVelocity");
                    ship.Orientation.Set(V(sample, "EndForward"), V(sample, "EndUp"));
                    var rates = sample.GetProperty("EndTurnRates").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    for (int axis = 0; axis < 3; axis++) ship.SetTurnRate(axis, rates[axis]);
                    outcomes.Add(new Trial(ship, sample.GetProperty("Ship").GetInt16(), server.SimNow));
                });
                // Allow the authoritative ejection and restored pose to reach the controller.
                Thread.Sleep(100);
            }
            ticker.Invoke(() => ticker.Observe = () =>
            {
                foreach (var trial in outcomes.Where(t => t.Outcome is null))
                {
                    var ship = trial.Ship;
                    var home = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == ship.SideId);
                    trial.MinDistance = Math.Min(trial.MinDistance, (ship.Position - home.Center).Length());
                    if (ship.StationId is not null) trial.Outcome = "docked";
                    else if (!ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod) || ship.Dead) trial.Outcome = "death/restart";
                    else if (server.SimNow - trial.Start >= 301) trial.Outcome = "oxygen/censored";
                    if (trial.Outcome is not null) { trial.Seconds = server.SimNow - trial.Start; trial.End = A(ship.Position); }
                }
            });
            var watch = Stopwatch.StartNew();
            double next = 0;
            while (watch.Elapsed.TotalSeconds < 330 && outcomes.Any(t => t.Outcome is null))
            {
                ticker.Check();
                if (watch.Elapsed.TotalSeconds >= next)
                { progress.WriteLine($"{label}: wall={watch.Elapsed.TotalSeconds:F0}s completed={outcomes.Count(t => t.Outcome is not null)}/{outcomes.Count}"); next += 30; }
                Thread.Sleep(100);
            }
            ticker.Invoke(() => ticker.Observe = null);
            // Docked station state can also be an oxygen restart. Reconcile
            // against the server's actual trip completion before claiming a
            // successful flight, matching the ejection time as well as ship ID.
            foreach (var line in File.ReadLines(Path.Combine(directory, label + ".log")))
            {
                if (!line.StartsWith("[pod-trip] {", StringComparison.Ordinal)) continue;
                using var receipt = JsonDocument.Parse(line[11..]);
                var record = receipt.RootElement;
                var trial = outcomes.SingleOrDefault(t => t.Ship.Id == record.GetProperty("Ship").GetInt16()
                    && Math.Abs(t.Start - record.GetProperty("EjectAt").GetDouble()) < .001);
                if (trial is null) continue;
                trial.Outcome = record.GetProperty("Outcome").GetString();
                trial.Seconds = record.GetProperty("Seconds").GetDouble();
                trial.End = record.GetProperty("EndPosition").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                trial.Stage = new[] { "Bay", "Radius", "Home" }.FirstOrDefault(s =>
                    record.GetProperty(s + "At").ValueKind != JsonValueKind.Null) ?? "Away";
                trial.Confirmed = true;
            }
            File.WriteAllText(Path.Combine(directory, label + ".json"), JsonSerializer.Serialize(new
            {
                Seed = 925, NativeTurn = nativeTurn, Swap = swap, Failures = server.SimPassStageFailures,
                Trials = outcomes.Select(t => new { t.Sample, Ship = t.Ship.Id, t.Outcome, t.Seconds, t.Stage, t.Confirmed, t.MinDistance, t.End }).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (server.SimPassStageFailures != 0 || outcomes.Any(t => t.Outcome is null || !t.Confirmed)) throw new InvalidOperationException("incomplete loopback");
        }
        finally { Console.SetOut(progress); }
    }

    private static void RunStrike(StaticCore core, Srv server, Mission mission, BotManager manager,
        Ticker ticker, string directory, string label, TextWriter progress, string targetHome)
    {
        var trials = new List<StrikeTrial>();
        foreach (short attacker in new short[] { 0, 1 })
        {
            var station = mission.Sectors.SelectMany(s => s.StationRuntimes).Single(s => s.SideId != attacker);
            var home = mission.Sectors.Single(s => s.Id == station.Data.ClusterId);
            if (home.Name != targetHome) continue;
            string border = home.Name == "Ganymede" ? "Bug Nebula" : "Tellus";
            var source = mission.Sectors.Single(s => s.Name == border);
            var entry = source.Warps.Single(w => w.Destination == home);
            var members = new List<Ship>();
            for (int i = 0; i < 3; i++)
            {
                // Identical native preferred Phoenix bomber loadout in both
                // directions. Harness bypasses purchase tech only; all flight,
                // warp handshake, firing and station damage are production.
                var loadout = Catalog.NativePreferredLoadout(195, core.HullTypes[195].Name);
                var spawn = manager.SpawnBot(loadout.HullId, loadout.HullName, attacker, loadout.Parts,
                    "combat", home.Id);
                lock (typeof(BotManager).GetField("_gate", Hidden)!.GetValue(manager)!)
                {
                    var bot = ((IEnumerable)typeof(BotManager).GetField("_bots", Hidden)!.GetValue(manager)!).Cast<object>()
                        .Single(b => (short)b.GetType().GetField("ShipId")!.GetValue(b)! == spawn.ShipId);
                    bot.GetType().GetField("TechLoadouts")!.SetValue(bot, false);
                    PinLoiter(bot, spawn.ShipId);
                }
                ticker.Invoke(() =>
                {
                    foreach (var st in mission.Sectors.SelectMany(s => s.StationRuntimes))
                        st.SideVisibility.SetSideVisibility(attacker, true);
                    var ship = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).Single(s => s.Id == spawn.ShipId);
                    if (ship.HullType.HullId != 195) throw new InvalidOperationException("strike fixture must grant real bombers");
                    var old = ship.Sector!;
                    old.Remove(ship); mission.PlaceShip(ship, source);
                    var p = new Vector3(entry.Warp.Position[0], entry.Warp.Position[1], entry.Warp.Position[2]);
                    ship.Position = p - p.Normalize() * (500 + 100 * i);
                    ship.Velocity = Vector3.Zero; ship.Orientation.Set((p - ship.Position).Normalize());
                    ship.LastWarpTime = double.NegativeInfinity;
                    typeof(Srv).GetMethod("OnShipWarped", Hidden)!.Invoke(server, [ship, old, source]);
                    members.Add(ship);
                });
            }
            trials.Add(new StrikeTrial(station, members, server.SimNow));
        }
        if (trials.Count != 1) throw new ArgumentException("target-home must be Ganymede or Organia");
        ticker.Invoke(() =>
        {
            // Start both measurements at the same barrier after fixture setup.
            foreach (var t in trials) t.Start = server.SimNow;
            ticker.Observe = () =>
            {
                foreach (var t in trials)
                {
                    int onTarget = t.Members.Count(s => !s.Dead && s.HullType.HullId == 195 && s.Sector?.Id == t.Station.Data.ClusterId);
                    t.Peak = Math.Max(t.Peak, onTarget);
                    if (onTarget > 0) t.Arrival ??= server.SimNow - t.Start;
                    if (t.Station.ArmorFraction < 1 || t.Station.ShieldFraction < 1) t.FirstDamage ??= server.SimNow - t.Start;
                }
            };
        });
        var watch = Stopwatch.StartNew(); double next = 0;
        while (watch.Elapsed.TotalSeconds < 180 && trials.Any(t => t.FirstDamage is null))
        {
            ticker.Check();
            if (watch.Elapsed.TotalSeconds >= next)
            { progress.WriteLine($"{label}: wall={watch.Elapsed.TotalSeconds:F0}s damaged={trials.Count(t => t.FirstDamage is not null)}/{trials.Count}"); next += 30; }
            Thread.Sleep(100);
        }
        ticker.Invoke(() => ticker.Observe = null);
        File.WriteAllText(Path.Combine(directory, label + ".json"), JsonSerializer.Serialize(new
        {
            Seed = 925, Defenders = 0, BomberHull = 195,
            RequestedLoadout = Catalog.NativePreferredLoadout(195, core.HullTypes[195].Name).Parts,
            LimitSeconds = 180, Failures = server.SimPassStageFailures,
            Trials = trials.Select(t => new { Home = t.Station.Data.ClusterId, t.Station.StationId,
                Type = t.Station.Type.StationTypeId, t.Start, t.Arrival, t.FirstDamage, t.Peak,
                EndHull = t.Station.ArmorFraction, EndShield = t.Station.ShieldFraction,
                Members = t.Members.Select(s => new { s.Id, Hull = s.HullType.HullId, Sector = s.Sector?.Id, End = A(s.Position),
                    Parts = s.Parts.Select(p => new { p.Type.PartId, p.MountId, p.Amount }).ToArray() }).ToArray() }).ToArray(),
            Bots = manager.ActiveBots(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (server.SimPassStageFailures != 0) throw new InvalidOperationException("strike simulation failed");
    }

    private sealed class StrikeTrial(Station station, List<Ship> members, double start)
    {
        internal readonly Station Station = station;
        internal readonly List<Ship> Members = members;
        internal double Start = start;
        internal double? Arrival, FirstDamage;
        internal int Peak;
    }

    private sealed class Trial(Ship ship, short sample, double start)
    {
        internal readonly Ship Ship = ship;
        internal readonly short Sample = sample;
        internal readonly double Start = start;
        internal string? Outcome;
        internal string? Stage;
        internal bool Confirmed;
        internal double Seconds;
        internal float MinDistance = float.MaxValue;
        internal float[] End = [];
    }

    private static Vector3 V(JsonElement sample, string name)
    { var a = sample.GetProperty(name).EnumerateArray().Select(x => x.GetSingle()).ToArray(); return new(a[0], a[1], a[2]); }
    private static float[] A(Vector3 v) => [v.X, v.Y, v.Z];
    private static void PinLoiter(object bot, short id)
    {
        bot.GetType().GetField("WeavePhase")!.SetValue(bot, new Random(925 + id).NextDouble() * 2 * Math.PI);
        bot.GetType().GetField("WeaveFreq")!.SetValue(bot, .037);
        bot.GetType().GetField("WeaveAmp")!.SetValue(bot, .03);
    }

    private sealed class Ticker : IDisposable
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Done)> _actions = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private Exception? _error;
        internal Action? Observe;
        internal Ticker(Srv server)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        while (_actions.TryDequeue(out var work))
                        { try { work.Action(); work.Done.SetResult(); } catch (Exception e) { work.Done.SetException(e); } }
                        server.TickSim(); Observe?.Invoke(); Thread.Sleep(50);
                    }
                }
                catch (Exception e) { _error = e; }
            }) { IsBackground = true };
            _thread.Start();
        }
        internal void Check() { if (_error is not null) throw new InvalidOperationException("ticker failed", _error); }
        internal void Invoke(Action action)
        { Check(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _actions.Enqueue((action, done)); done.Task.Wait(TimeSpan.FromSeconds(15)); done.Task.GetAwaiter().GetResult(); }
        public void Dispose() { _stop.Cancel(); if (!_thread.Join(5000)) throw new TimeoutException("ticker shutdown"); _stop.Dispose(); Check(); }
    }
}
