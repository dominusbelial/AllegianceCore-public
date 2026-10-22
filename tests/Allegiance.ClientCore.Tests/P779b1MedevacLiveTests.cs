using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P779b-1 — the LIVE loopback proof of the pod half: a real MCP combat bot is
/// ejected into a real lifepod in a NON-home sector, a real type-19 "Rescue"
/// probe is deployed 600 u away through the real FIRE_EXPENDABLE path (P780's
/// DeployRescueProbe recipe, including its retry and ship-id resolution), and
/// the bot's own pod autopilot must fly INTO the probe and be rescued by the
/// server's P780 probe pass — logging <c>rescued by rescue probe</c>. On the
/// unfixed code the pod targets only restart-capable stations and never touches
/// the probe.
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P779b1MedevacLiveTests(ITestOutputHelper output)
{
    private const short FlySector = 1052;   // no station (m2xspiderw terrain known to the pod tests)

    private static ServerConfig Config() => new()
    {
        ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
        Port = 0, UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // these clients never send POSITIONREQ
        LiveSettings = new() { TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true } },
        Games = [new() { Name = "P779b1", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3],
            SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff }],
    };

    private sealed class Ticker : IDisposable
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Done)> _actions = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private readonly Srv _server;
        public Ticker(Srv server)
        {
            _server = server;
            _thread = new Thread(() =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    while (_actions.TryDequeue(out var work))
                    { try { work.Action(); work.Done.SetResult(); } catch (Exception e) { work.Done.SetException(e); } }
                    server.TickSim(); Thread.Sleep(10);
                }
            }) { IsBackground = true };
            _thread.Start();
        }
        public void Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _actions.Enqueue((action, done)); Assert.True(done.Task.Wait(15000));
        }
        public T Invoke<T>(Func<T> action)
        {
            T result = default!;
            Invoke(() => { result = action(); });
            return result;
        }
        public void Dispose() { _stop.Cancel(); _thread.Join(5000); _stop.Dispose(); }
    }

    /// <summary>The bot's own combat session (the connection that must see the
    /// probe and whose controller flies the pod).</summary>
    private static ConnectionSession SessionOf(BotManager manager, short shipId)
    {
        var bots = ((System.Collections.IEnumerable)typeof(BotManager)
            .GetField("_bots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!)
            .Cast<object>().ToArray();
        var bot = bots.Single(b => (short)b.GetType().GetField("ShipId")!.GetValue(b)! == shipId);
        return (ConnectionSession)bot.GetType().GetField("Cs")!.GetValue(bot)!;
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Pod_flies_into_an_own_side_rescue_probe_and_is_rescued_across_600u()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        Assert.True(manager.ServerReady);

        var hull = EconomyLoadoutTestSetup.WeaponHull(mission.Core);
        var spawn = manager.SpawnBot(hull.HullId, hull.Name, 0, [], "combat", role: "scout");
        var session = SessionOf(manager, spawn.ShipId);

        Ship ship = null!;
        Sector sector = null!;
        Vector3 probeAt = default;
        ticker.Invoke(() =>
        {
            ship = server.Ships.Single(l => l.Ship.Id == spawn.ShipId).Ship;
            // A real sector with NO side-0 station. Launch sectors are skipped so
            // the pod demonstrably prefers the probe over a nearby station.
            sector = mission.Sectors.First(s => s.Id != FlySector
                && !s.StationRuntimes.Any(st => st.SideId == ship.SideId));
            var previous = ship.Sector!;
            previous.Remove(ship);
            mission.PlaceShip(ship, sector);
            ship.Position = sector.Warps.Select(w => w.Warp).FirstOrDefault() is { } warp
                ? new Vector3(warp.Position[0], warp.Position[1], warp.Position[2]) + new Vector3(0, 0, 400)
                : new Vector3(0, 500, 0);
            ship.Velocity = Vector3.Zero;
            ship.Orientation.Set(new Vector3(0, 0, -1), new Vector3(0, 1, 0));
            typeof(Srv).GetMethod("OnShipWarped", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [ship, previous, sector]);
            ship.LastWarpTime = double.NegativeInfinity;
            probeAt = ship.Position;
        });

        // P780's real deploy: a mounted type-19 launcher plus the real
        // FIRE_EXPENDABLE request, re-sent until the probe appears (the helper's
        // flake fix). The deployer is resolved by the ship id it actually owns.
        Probe probe = null!;
        var core = mission.Core;
        var type = core.ProbeTypes[(short)19];
        Assert.Equal(DataProbeTypeIGC.EabmRescue, type.EabmCapabilities);
        var part = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "rescue", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 778, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ProbeLauncher(type.ExpendableTypeId));
        ticker.Invoke(() =>
        {
            ship.TryRemovePart(EquipmentType.Dispenser, 0, out _);
            ship.MountPart(part, amount: 3, mountId: 0, fractionMounted: 255);
        });
        for (int attempt = 0; attempt < 3 && probe is null; attempt++)
        {
            session.SendFireExpendable(EquipmentType.Dispenser);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4000 && probe is null)
            {
                Thread.Sleep(50);
                ticker.Invoke(() =>
                {
                    var p = sector.Models.OfType<Probe>()
                        .FirstOrDefault(x => x.ProbeType.ExpendableTypeId == 19);
                    if (p is not null) probe = p;
                });
            }
        }
        Assert.NotNull(probe);
        ticker.Invoke(() => probeAt = probe.Position);
        output.WriteLine($"probe {probe.Id} ({type.LauncherName}) at {probeAt}, side {probe.SideId}");

        // The bot's ClientWorld must SEE the probe before its pod can target it.
        var probeSeen = Stopwatch.StartNew();
        bool seen = false;
        while (probeSeen.Elapsed.TotalSeconds < 10 && !seen)
        {
            Thread.Sleep(50);
            lock (session.Gate) seen = session.World.Probes.ContainsKey(probe.Id);
        }
        Assert.True(seen, $"bot's world never received EXPORT_PROBE for probe {probe.Id}");

        // Watch the server log, not just ship.StationId: the bot's pod cycle
        // relaunches within a tick or two of the dock, so the docked field is
        // already cleared again by the time a poll sees it. Capture starts
        // BEFORE the eject so the [p779b] target line cannot be missed.
        var log = new CapturingWriter();
        var oldOut = Console.Out;
        var waited = Stopwatch.StartNew();
        bool rescued = false;
        try
        {
            Console.SetOut(log);
            // Eject ~600 u away in the SAME sector, then watch the pod fly into it.
            ticker.Invoke(() =>
            {
                ship.Position = probeAt + new Vector3(600, 0, 0);
                ship.Velocity = Vector3.Zero;
                ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, Vector3.Zero, null);
                Assert.True(ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
            });
            while (waited.Elapsed.TotalSeconds < 60 && !rescued)
            {
                Thread.Sleep(100);
                rescued = log.Contains("rescued by rescue probe");
            }
        }
        finally { Console.SetOut(oldOut); }
        var captured = log.ToString();
        output.WriteLine($"rescued={rescued} elapsed={waited.Elapsed.TotalSeconds:F1}s");
        output.WriteLine(captured.Split('\n').Where(l => l.Contains("[p779b]") || l.Contains("[rescue]") || l.Contains("[p539]"))
            .Aggregate("", (a, b) => a + b + "\n"));

        Assert.True(rescued, "the pod never touched the rescue probe");
        Assert.Contains("[p779b] pod bot", captured);
        Assert.Contains("-> rescue probe", captured);
        Assert.Equal(0, server.SimPassStageFailures);
    }

    /// <summary>A thread-safe console capture: the server ticker and the bot loop
    /// both write while the test polls for the rescue line.</summary>
    private sealed class CapturingWriter : TextWriter
    {
        private readonly StringWriter _inner = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) { lock (_inner) _inner.Write(value); }
        public override void Write(string? value) { lock (_inner) _inner.Write(value); }
        public override void WriteLine(string? value) { lock (_inner) _inner.WriteLine(value); }
        public bool Contains(string text) { lock (_inner) return _inner.ToString().Contains(text); }
        public override string ToString() { lock (_inner) return _inner.ToString(); }
    }
}
