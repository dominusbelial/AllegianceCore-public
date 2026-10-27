using System.Diagnostics;
using System.Text.Json;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

/// <summary>Headless bundle smoke and reproducible local benchmark. Uses the same host as the menu.</summary>
public static class SkirmishSmoke
{
    public sealed record Result(double WallSeconds, double SimulationSeconds, bool BotsAlive, bool Running,
        int Bots, long Shots, long Hits, double PilotDistance, int[] Processes, short PilotShipId);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args[0] == "--skirmish-owner-proof")
            {
                using var host = await SkirmishHost.StartAsync(new() { TechMode = "unlocked" });
                await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { pids = host.BotProcessIds, port = host.Port }));
                await Task.Delay(Timeout.Infinite);
                return 0;
            }
            double seconds = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 60;
            if (!double.IsFinite(seconds) || seconds < 1 || seconds > 3600) throw new ArgumentException("Smoke duration must be 1..3600 seconds");
            var size = args.Length > 2 ? Enum.Parse<SkirmishSize>(args[2], true) : SkirmishSize.Small;
            // Optional native goal preset, after the existing duration and size arguments.
            var goals = args.Length > 3 ? MissionGoals.Preset(args[3]) : new MissionGoals();
            using var game = await SkirmishHost.StartAsync(new() { Size = size, TechMode = "unlocked", Goals = goals });
            var result = await PlayAsync(game, TimeSpan.FromSeconds(seconds));
            Console.WriteLine("[skirmish-smoke] " + JsonSerializer.Serialize(result));
            return result.BotsAlive && result.Running && result.Bots == game.Options.TeamSize * 2 - 1
                && result.PilotDistance > 1 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine("[skirmish-smoke] " + ex); return 1; }
    }

    /// <summary>Test-only pilot: seats on side zero, readies, and flies ordinary control packets.</summary>
    public static async Task<Result> PlayAsync(SkirmishHost host, TimeSpan duration, CancellationToken ct = default, Allegiance.ClientCore.Replay.WireRecording? recording = null, Action? onBattleStarted = null, bool benchmarkCamera = false)
    {
        // The smoke always flies a pilot, including sizes that now default to observing.
        await host.SetPlayerSideAsync(0, ct);
        using var pilot = new ConnectionSession { Recording = recording };
        string pilotName = benchmarkCamera ? Benchmark.BenchmarkCommand.PilotName : "skirmish-test-pilot";
        pilot.Connect(host.Port, pilotName);
        pilot.SendLogonRequest(new()
        {
            CharacterName = pilotName, FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = 727, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 72701
        });
        pilot.WaitForAck(10000);
        pilot.SendPositionReq(0);
        var startup = Stopwatch.StartNew();
        while (true)
        {
            lock (pilot.Gate)
            {
                if (pilot.World.MyShipId is { } id)
                {
                    if (pilot.World.MySideId != 0) throw new IOException("Pilot joined wrong side");
                    pilot.SendPlayerReady(true, id);
                    break;
                }
                if (pilot.LastPositionRefusal is { } refused) throw new IOException("Pilot seat refused: " + refused.Reason);
            }
            if (startup.Elapsed.TotalSeconds > 20) throw new TimeoutException("Pilot was not seated");
            await Task.Delay(50, ct);
        }
        if (onBattleStarted is not null)
        {
            var battleWait = Stopwatch.StartNew();
            long nextLaunchMs = 0;
            while (true)
            {
                bool launched = false;
                lock (pilot.Gate)
                {
                    if (pilot.World.MyShipId is { } id && pilot.World.Ships.TryGetValue(id, out var ship)
                        && pilot.World.Mission?.Stage == MissionStageMessage.StageStarted)
                    {
                        launched = ship.StationId < 0 && ship.Latest is not null;
                        if (!launched && battleWait.ElapsedMilliseconds >= nextLaunchMs)
                        { pilot.SendBuyLoadout(ship.HullId, true, ship.Loadout); nextLaunchMs = battleWait.ElapsedMilliseconds + 1000; }
                    }
                }
                if (launched) break;
                if (battleWait.Elapsed.TotalSeconds > 90 || !host.BotsAlive) throw new TimeoutException("Benchmark battle did not launch");
                await Task.Delay(50, ct);
            }
            onBattleStarted();
        }
        var watch = Stopwatch.StartNew();
        Allegiance.Core.Collision.Vector3? initial = null;
        double distance = 0;
        double nextLaunch = 0;
        byte lastCameraPhase = 255;
        double nextViewRequest = 0;
        while (watch.Elapsed < duration)
        {
            if (!host.BotsAlive) throw new IOException(host.Failure ?? "Bots stopped");
            lock (pilot.Gate)
            {
                if (pilot.World.MyShipId is { } id && pilot.World.Ships.TryGetValue(id, out var ship) && ship.Latest is { } pose)
                {
                    // P799 — ghost guard: a pilot out of lives is a ghost
                    // (deaths > iLives, shipIGC.h:2465-2469); the server refuses
                    // every BUY_LOADOUT (FedSrv.CPP:6156), so stop retrying.
                    bool pilotGhost = pilot.World.TryGetOwnMissionStats(out _, out short pilotDeaths)
                        && pilotDeaths > pilot.World.MissionLives;
                    if (ship.StationId >= 0 && !pilotGhost && watch.Elapsed.TotalSeconds >= nextLaunch
                        && pilot.World.Mission?.Stage == MissionStageMessage.StageStarted)
                    {
                        pilot.SendBuyLoadout(ship.HullId, true, ship.Loadout);
                        nextLaunch = watch.Elapsed.TotalSeconds + 2;
                    }
                    if (benchmarkCamera)
                    {
                        byte phase = Allegiance.ClientCore.Replay.BenchmarkRoute.Phase(watch.Elapsed.TotalSeconds, duration.TotalSeconds);
                        if (phase != lastCameraPhase || watch.Elapsed.TotalSeconds >= nextViewRequest)
                        {
                            short home = pilot.World.Stations.Values.Where(s => s.SideId == 0).OrderBy(s => s.StationId).First().ClusterId;
                            short battle = pilot.World.Clusters.Values.First(s => s.Name == "Alderan").ClusterId;
                            short sector = phase == 1 ? battle : home;
                            pilot.SendViewClusterRequest(sector);
                            pilot.World.TelemetrySectorOverride = sector;
                            if (phase != lastCameraPhase) recording?.WriteCamera(sector, phase);
                            lastCameraPhase = phase; nextViewRequest = watch.Elapsed.TotalSeconds + 2;
                        }
                    }
                    host.SetFocusSector(benchmarkCamera ? pilot.World.TelemetrySectorOverride ?? ship.SectorId : ship.SectorId);
                    initial ??= pose.Position;
                    distance = Math.Max(distance, (pose.Position - initial.Value).Length());
                    // Steer toward the closest visible enemy; otherwise fly a broad patrol.
                    var enemy = pilot.World.Ships.Values.Where(s => s.SideId == 1 && !s.IsNotVisible && s.Latest is not null && s.SectorId == ship.SectorId)
                        .OrderBy(s => (s.Latest!.Position - pose.Position).LengthSquared()).FirstOrDefault();
                    var controls = enemy?.Latest is { } target
                        ? AutopilotSteering.Pursue(pose.Position, pose.Orientation, target.Position)
                        : new ControlData(.12f, .04f, 0, 1);
                    pilot.CommandControls(benchmarkCamera ? new ControlData(0, 0, 0, 1) : controls,
                        benchmarkCamera || enemy is null ? 0 : ShipControlState.AllWeapons);
                }
                if (watch.ElapsedMilliseconds % 2000 < 60) pilot.SendPing();
            }
            await Task.Delay(50, ct);
        }
        var stats = host.Stats;
        long Count(string key) => stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty("combat", out var combat)
            && combat.TryGetProperty(key, out var value) ? (long)value.GetDouble() : 0;
        int bots = stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty("botCount", out var count) ? count.GetInt32() : 0;
        bool running;
        short pilotShip;
        lock (pilot.Gate)
        {
            running = !pilot.Lost && pilot.World.Mission?.Stage == MissionStageMessage.StageStarted;
            pilotShip = pilot.World.MyShipId ?? -1;
        }
        return new(watch.Elapsed.TotalSeconds, host.SimulationSeconds, host.BotsAlive, running, bots,
            Count("shotsFired"), Count("shipHits"), distance, host.BotProcessIds, pilotShip);
    }
}
