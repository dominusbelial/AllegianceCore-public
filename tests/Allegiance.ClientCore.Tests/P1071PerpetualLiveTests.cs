using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P1071PerpetualLiveTests(ITestOutputHelper output)
{
    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent!;
        return dir.FullName;
    }

    internal static ServerConfig Template() => ServerConfigLoader.Load(
        Path.Combine(Root(), "docker/public/server.perpetual.example.json"), []);

    [SkippableFact]
    public void Public_example_boots_legal_seeded_civs_and_cap_auto_next_starts_second_match()
    {
        Require.Artwork();
        var config = Template();
        Assert.Equal(new[] { -1, -1 }, config.Games[0].CivIds);
        Assert.Equal(1, config.Games[0].MaxTeamImbalance);
        Assert.Equal(3600, config.Showcase.MatchCapSeconds);
        Assert.True(config.Identity!.AllowAnonymous);
        Assert.True(config.PlayLog.Enabled);
        Assert.True(config.LiveSettings!.AutoNextMatch!.Enabled);
        Assert.Equal(30, config.LiveSettings.AutoNextMatch.IntermissionSeconds);
        Assert.Equal(32, config.Transport!.MaxConnectionsPerIp);
        // Local bindings replace only deployment paths/identity. The full 3600s
        // cap and 30s intermission remain intact; TickSim runs without cadence sleep.
        config.ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        config.Port = 0; config.WebSocket.Enabled = false; config.Lobby = null; config.Identity = null;
        config.MinimumClientVersion = null;
        config.UpdatesPerSecond = 2; config.LiveSettings.TimeDilation = "off";
        config.Games[0].CivSeed = 1071;
        string directory = Path.Combine(Path.GetTempPath(), "p1071-live-" + Guid.NewGuid().ToString("N"));
        config.PlayLog.Path = Path.Combine(directory, "plays.jsonl");
        var saved = Console.Out;
        using var captured = new StringWriter();
        using var log = TextWriter.Synchronized(captured);
        Console.SetOut(log);
        try
        {
            using (var server = new Srv())
            {
                server.Boot(config);
                using var a = P660NextMatchLiveTests.Connect(server, "Pilot-Cap-A", 107101);
                using var b = P660NextMatchLiveTests.Connect(server, "Pilot-Cap-B", 107102);
                a.SendPlayerReady(true, a.World.MyShipId!.Value);
                b.SendPlayerReady(true, b.World.MyShipId!.Value);
                P660NextMatchLiveTests.Pump(server, () => server.Missions[0].Stage == MissionStage.Started);
                var first = server.Missions[0];
                Assert.Equal(3600, first.CountdownDuration);
                var firstCivs = first.Sides.Select(s => s.Civilization.CivilizationId).ToArray();
                var core = first.Core;
                var firstRows = first.Sides.Select(s => new PlayCiv(s.Id, s.Civilization.CivilizationId, s.Civilization.Name)).ToArray();
                Assert.All(firstCivs, id => Assert.Contains(id, Srv.LegalLobbyCivs(first.Core).Select(c => c.CivilizationId)));
                int ticks = 0;
                while (ReferenceEquals(first, server.Missions[0]) && ticks++ < 8000)
                {
                    server.TickSim();
                    if (ticks % 100 == 0) Thread.Sleep(1);
                }
                Assert.NotSame(first, server.Missions[0]);
                Assert.Equal(MissionStage.Over, first.Stage);
                P660NextMatchLiveTests.Pump(server, () => server.Missions[0].Stage == MissionStage.Started);
                var second = server.Missions[0];
                Assert.Equal(3600, second.CountdownDuration);
                Assert.All(second.Sides, side => Assert.Contains(side.Civilization.CivilizationId,
                    Srv.LegalLobbyCivs(second.Core).Select(c => c.CivilizationId)));
                string[] matchupLines = captured.ToString()!.Split('\n').Where(l => l.StartsWith("[matchup]")).ToArray();
                Assert.Equal(2, matchupLines.Length);
                var secondRows = second.Sides.Select(s => new PlayCiv(s.Id, s.Civilization.CivilizationId, s.Civilization.Name)).ToArray();
                foreach (var (line, civs) in matchupLines.Zip(new[] { firstRows, secondRows }))
                {
                    int seed = int.Parse(Regex.Match(line, @"seed=(\d+)").Groups[1].Value);
                    Assert.Equal(MatchCivilizations.Resolve(config.Games[0], core, seed), civs.Select(c => c.Id));
                    Assert.All(civs, c => Assert.Contains($"{c.Id}:{c.Name}", line));
                    output.WriteLine(line.Trim());
                }
                Assert.Contains("[finale] cap winner", captured.ToString());
                Assert.Equal(0, server.SimPassStageFailures);
                output.WriteLine($"Full cap advanced in {ticks} ticks; second start={second.CountdownStart}; imbalance={config.Games[0].MaxTeamImbalance}");
            }
            var rows = Directory.GetFiles(directory).SelectMany(File.ReadAllLines).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                Assert.Equal(2, rows.Count(r => r.RootElement.TryGetProperty("type", out _)));
                Assert.Equal(4, rows.Count(r => !r.RootElement.TryGetProperty("type", out _))); // one segment/player/match
            }
            finally { foreach (var row in rows) row.Dispose(); }
        }
        finally { Console.SetOut(saved); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [SkippableFact]
    public async Task Real_backfill_yields_combat_refills_after_grace_and_preserves_humans_across_rotation()
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(true, intermission: 2);
        config.Showcase = new() { TechMode = "research", MatchCapSeconds = 0, IncomeMultiplier = 5000 };
        config.LiveSettings!.TestHarness = null;
        config.Games[0].MaxTeamImbalance = 1;
        config.Games[0].CivIds = [25, 25];
        config.Games[0].StartCountdownSeconds = 1;
        config.BenchmarkSeed = 1071;
        config.Games[0].CivSeed = 1071;
        using var trustKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string trustFile = Path.GetTempFileName();
        File.WriteAllText(trustFile, trustKey.ExportSubjectPublicKeyInfoPem());
        config.Identity = new() { Issuer = "p1071-loopback", Audience = "game", PublicKeyFile = trustFile, AllowAnonymous = true };
        config.MinimumClientVersion = "2.0.0";
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        var gate = new object();
        using var tickStop = new CancellationTokenSource();
        Exception? tickError = null;
        var ticker = new Thread(() =>
        {
            try { while (!tickStop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(25); } }
            catch (Exception ex) { tickError = ex; }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("localhost", server.BoundPort, backfill: true, clientVersion: "2.0.0");
            var driver = new BackfillDriver(manager, 3);
            using var driverStop = new CancellationTokenSource();
            var driving = driver.RunAsync(driverStop.Token);
            try
            {
                async Task Until(Func<bool> done, double seconds = 20)
                {
                    var watch = Stopwatch.StartNew();
                    while (!done() && watch.Elapsed.TotalSeconds < seconds)
                    {
                        Assert.Null(tickError);
                        Assert.False(driving.IsFaulted, driving.Exception?.ToString());
                        await Task.Delay(50);
                    }
                    Assert.True(done(), "roster deadline exceeded: " + JsonSerializer.Serialize(manager.BackfillRoster(BotNameRules.Defaults()).Pilots));
                }
                BackfillPilot[] Roster() => manager.BackfillRoster(BotNameRules.Defaults()).Pilots;
                bool Balanced(int humans0, int humans1) => new short[] { 0, 1 }.All(side =>
                    Roster().Count(p => p.Side == side) == 3 && Roster().Count(p => p.Side == side && !p.Bot) == (side == 0 ? humans0 : humans1));
                await Until(() => Balanced(0, 0) && Roster().Count(p => p.Owned) == 4 && manager.BackfillFinaleCoverage().Length == 4);
                var protectedNames = Roster().Where(p => p.Protected).Select(p => p.Name).ToArray();
                Assert.Equal(2, protectedNames.Length);
                output.WriteLine("initial roster: " + JsonSerializer.Serialize(Roster()));
                async Task<ConnectionSession> Human(string name, short side)
                {
                    var peer = new ConnectionSession();
                    peer.Connect(server.BoundPort, name, "localhost");
                    peer.SendLogonRequest(new() { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, Time = 1, ClientVersion = "2.0.0" });
                    await Until(() => peer.Ack is { FValidated: true });
                    peer.SendPositionReq(side);
                    await Until(() => peer.World.MySideId is >= 0);
                    return peer;
                }
                using var a = await Human("Pilot-Backfill-A", 0);
                var eviction = Stopwatch.StartNew();
                await Until(() => Balanced(1, 0), 5);
                output.WriteLine($"human join yielded combat bot in {eviction.Elapsed.TotalSeconds:F2}s");
                Assert.Equal(2, Roster().Count(p => p.Side == 0 && p.Bot));
                Assert.All(protectedNames, name => Assert.Contains(Roster(), p => p.Name == name));
                // Once backfill restores 3/3, automatic selection has a tie:
                // native does not promise the next human side 1. Exercise an
                // ordinary explicit side-1 selection, preserving that rule.
                using var b = await Human("Pilot-Backfill-B", 1);
                Assert.Equal((short)1, b.World.MySideId);
                await Until(() => Balanced(1, 1), 5);
                a.SendLogoff();
                await Until(() => Roster().All(p => p.Name != "Pilot-Backfill-A"));
                var grace = Stopwatch.StartNew();
                await Task.Delay(28000);
                Assert.Equal(2, Roster().Count(p => p.Side == 0));
                await Until(() => Balanced(0, 1) && Roster().Count(p => p.Owned) == 3 && manager.BackfillFinaleCoverage().Length == 3, 10);
                Assert.Equal(Roster().Count(p => p.Owned), manager.BackfillFinaleCoverage().Length);
                output.WriteLine($"departure refill observed after {grace.Elapsed.TotalSeconds:F2}s (30s grace); replacement retained finale membership");
                // Advance only the existing preset's eight-minute discovery
                // timer, under its owner pause gates. Keep both real 30s seat
                // graces below; no visibility or server admission rule is bypassed.
                manager.SetPaused(true);
                try
                {
                    var pending = (List<(ShowcaseFinale.Settings Settings, string[] Names, double PendingAt)>?)typeof(BotManager)
                        .GetField("_pendingFinales", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager);
                    if (pending is not null)
                        for (int i = 0; i < pending.Count; i++)
                            pending[i] = (pending[i].Settings, pending[i].Names, -ScoutOpening.PendingArmGraceSeconds - 1);
                }
                finally { manager.SetPaused(false); }
                await Until(() => manager.FinaleStatus().Length > 0, 10);
                long generation = manager.BackfillRoster(BotNameRules.Defaults()).Generation;
                lock (gate) P660NextMatchLiveTests.End(server);
                await Until(() => manager.BackfillRoster(BotNameRules.Defaults()).Generation > generation && Balanced(0, 1), 25);
                Assert.Equal((short)1, b.World.MySideId);
                Assert.All(protectedNames, name => Assert.Contains(Roster(), p => p.Name == name));
                output.WriteLine("rotated roster: " + JsonSerializer.Serialize(Roster()));
                b.SendLogoff();
                await Until(() => Roster().All(p => p.Name != "Pilot-Backfill-B"));
                await Task.Delay(28000);
                Assert.Equal(2, Roster().Count(p => p.Side == 1));
                await Until(() => Balanced(0, 0) && Roster().Count(p => p.Owned) == 4 && manager.BackfillFinaleCoverage().Length == 4, 15);
                output.WriteLine("post-rotation refill retained four current-world finale combat slots");
                generation = manager.BackfillRoster(BotNameRules.Defaults()).Generation;
                lock (gate) P660NextMatchLiveTests.End(server);
                await Until(() => manager.BackfillRoster(BotNameRules.Defaults()).Generation > generation && Balanced(0, 0), 25);
                Assert.All(protectedNames, name => Assert.Contains(Roster(), p => p.Name == name));
            }
            finally
            {
                driverStop.Cancel();
                try { await driving; } catch (OperationCanceledException) { }
            }
            Assert.Null(tickError);
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { tickStop.Cancel(); Assert.True(ticker.Join(10000)); File.Delete(trustFile); }
    }

    [SkippableFact]
    public void Auto_pick_counts_bots_and_humans_and_preserves_native_low_rank_tolerance()
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(false);
        config.SideSelectAutoSeatSeconds = 0;
        config.LiveSettings!.TestHarness = null; // name prefixes grant no numeric-gate bypass
        config.Games[0].MaxTeamImbalance = 1;
        config.Games[0].AutoStart = false;
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        var peers = new List<ConnectionSession>();
        try
        {
            ConnectionSession Join(string name, short side, bool accepted = true)
            {
                var peer = new ConnectionSession(); peers.Add(peer);
                peer.Connect(server.BoundPort, name);
                peer.SendLogonRequest(new() { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, Time = 1 });
                P660NextMatchLiveTests.Pump(server, () => peer.Ack is not null);
                Assert.True(peer.Ack!.FValidated);
                peer.SendPositionReq(side);
                P660NextMatchLiveTests.Pump(server, () => peer.World.MyShipId is not null && peer.World.MySideId is not null || peer.LastPositionRefusal is not null);
                if (accepted) Assert.NotNull(peer.World.MyShipId);
                else Assert.Equal(DelPositionReqReason.TeamBalance, peer.LastPositionRefusal!.Reason);
                return peer;
            }
            for (int i = 0; i < 3; i++)
            {
                Join($"mcp-roster-a{i}", 0);
                Join($"mcp-roster-b{i}", 1);
            }
            server.Missions[0].Stage = MissionStage.Started;
            Join("Pilot-First", 0); // 4/3, before any external backfill decision
            using var second = Join("Pilot-Auto", -1);
            Assert.Equal((short)1, second.World.MySideId); // ordinary auto-pick chooses smaller roster
            // The native low-rank exception (fsmission.cpp:4637-4642) permits
            // two players beyond the normal one-seat imbalance, then refuses.
            Join("Pilot-Normal", 0);
            Join("Pilot-Newbie1", 0);
            Join("Pilot-Newbie2", 0);
            Join("Pilot-Refused", 0, accepted: false);
            Assert.Equal(7, server.Ships.Count(s => s.Ship.SideId == 0));
            Assert.Equal(4, server.Ships.Count(s => s.Ship.SideId == 1));
            Assert.Equal(0, server.SimPassStageFailures);
            output.WriteLine("3 bot seats/side plus human on side 0 -> auto human on smaller side 1; native newbie tolerance retained, then TeamBalance refusal");
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }

    [SkippableFact]
    public async Task Websocket_session_records_browser_and_actual_launch_before_disconnect()
    {
        Require.Artwork();
        string dir = Path.Combine(Path.GetTempPath(), "p1071-ws-" + Guid.NewGuid().ToString("N"));
        var config = P660NextMatchLiveTests.Config(false);
        config.SideSelectAutoSeatSeconds = 0;
        config.Games[0].AllowEmptyTeams = true;
        config.Games[0].StartCountdownSeconds = 0;
        config.WebSocket = new() { Enabled = true, Port = 0, AllowedOrigins = ["http://localhost"], AllowLoopbackDevelopment = true };
        config.PlayLog = new() { Enabled = true, Path = Path.Combine(dir, "plays.jsonl") };
        try
        {
            using (var server = new Srv { SpawnInitialMinersAtBoot = false })
            {
                server.Boot(config);
                using var peer = new ConnectionSession
                {
                    WebSocketEndpoint = WebSocketEndpoint.FromConfiguration($"ws://localhost:{server.WebSocketPort}/game"),
                    WebSocketOrigin = "http://localhost",
                };
                async Task Pump(Func<bool> done)
                {
                    var watch = Stopwatch.StartNew();
                    while (!done() && watch.Elapsed.TotalSeconds < 10) { server.TickSim(); await Task.Delay(5); }
                    Assert.True(done(), "WebSocket session condition timed out");
                }
                var joining = peer.JoinAsync(server.BoundPort, "Pilot-Browser", new()
                { CharacterName = "Pilot-Browser", FedsrvVer = LogonRequest.MsgVer, Time = 1 });
                await Pump(() => joining.IsCompleted);
                Assert.Null(await joining);
                peer.SendPositionReq(0);
                await Pump(() => server.Ships.Count == 1 && peer.World.MyShipId is not null);
                var live = server.Ships.Single();
                Assert.NotNull(live.Ship.StationId);
                peer.SendPlayerReady(true, live.Ship.Id);
                await Pump(() => server.Missions[0].Stage == MissionStage.Started);
                peer.SendBuyLoadout(live.Ship.HullType.HullId, true);
                await Pump(() => live.Ship.StationId is null);
                // Use abrupt transport shutdown to exercise the disconnect path,
                // distinct from the explicit LOGOFF path in the identity test.
                await peer.ShutdownAsync();
                await Pump(() => server.Ships.Count == 0);
            }
            var lines = Directory.GetFiles(dir).SelectMany(File.ReadAllLines).ToArray();
            using var session = JsonDocument.Parse(lines[0]);
            Assert.Equal("browser", session.RootElement.GetProperty("client").GetString());
            Assert.True(session.RootElement.GetProperty("launched").GetBoolean());
            Assert.Equal("guest", session.RootElement.GetProperty("kind").GetString());
            Assert.Single(lines, line => !JsonDocument.Parse(line).RootElement.TryGetProperty("type", out _));
            // Last-player departure may finish and rotate the mission; its empty
            // successor also gets a shutdown summary, with no invented session.
            Assert.All(lines.Skip(1), line => Assert.Equal("match", JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [SkippableFact]
    public void Session_log_uses_verified_kind_score_deltas_and_omits_bots_and_credentials()
    {
        Require.Artwork();
        string dir = Path.Combine(Path.GetTempPath(), "p1071-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string keyFile = Path.Combine(dir, "key.pem"); File.WriteAllText(keyFile, key.ExportSubjectPublicKeyInfoPem());
        string account = Guid.NewGuid().ToString("N");
        string token = JoinTokens.Issue(key, "test-issuer", "test-server", account, "AccountPilot", DateTimeOffset.UtcNow);
        var config = P660NextMatchLiveTests.Config(false);
        config.SideSelectAutoSeatSeconds = 0;
        config.Identity = new() { Issuer = "test-issuer", Audience = "test-server", PublicKeyFile = keyFile, AllowAnonymous = true };
        config.PlayLog = new() { Enabled = true, Path = Path.Combine(dir, "plays.jsonl") };
        try
        {
            using (var server = new Srv { SpawnInitialMinersAtBoot = false })
            {
                server.Boot(config);
                ConnectionSession Join(string name, string? ticket, short? side = null)
                {
                    var peer = new ConnectionSession(); peer.Connect(server.BoundPort, name);
                    peer.SendLogonRequest(new() { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, Time = 1,
                        ZoneTicket = ticket is null ? null : Encoding.UTF8.GetBytes(ticket) });
                    P660NextMatchLiveTests.Pump(server, () => peer.Ack is not null);
                    Assert.True(peer.Ack!.FValidated, peer.Ack.CharNameOrFailureReason);
                    if (side is { } pick)
                    {
                        peer.SendPositionReq(pick);
                        P660NextMatchLiveTests.Pump(server, () => peer.World.MyShipId is not null);
                    }
                    return peer;
                }
                using var a = Join("AccountPilot", token, 0);
                var score = server.Ships.Single().PlayerScore;
                score.Kills = 4; score.Deaths = 2; score.Ejections = 3;
                a.SendLogoff();
                P660NextMatchLiveTests.Pump(server, () => server.Ships.Count == 0);
                using var rejoin = Join("AccountPilot", JoinTokens.Issue(key, "test-issuer", "test-server", account, "AccountPilot", DateTimeOffset.UtcNow), 0);
                Assert.Same(score, server.Ships.Single().PlayerScore);
                score.Kills++; score.Deaths++; score.Ejections++;
                rejoin.SendLogoff(); P660NextMatchLiveTests.Pump(server, () => server.Ships.Count == 0);
                using var pending = Join("Pilot-OnlyJoined", null);
                pending.SendLogoff();
                for (int i = 0; i < 10; i++) { server.TickSim(); Thread.Sleep(1); }
                using var bot = Join("MCP-bot-fixture", null, 1);
                bot.SendLogoff(); P660NextMatchLiveTests.Pump(server, () => server.Ships.Count == 0);
            }
            string[] lines = Directory.GetFiles(dir, "plays-*.jsonl").SelectMany(File.ReadAllLines).ToArray();
            string all = string.Join('\n', lines);
            Assert.DoesNotContain(account, all); Assert.DoesNotContain(token, all); Assert.DoesNotContain("MCP-bot-fixture", all);
            using var first = JsonDocument.Parse(lines[0]);
            Assert.Equal("account", first.RootElement.GetProperty("kind").GetString());
            Assert.Equal("desktop", first.RootElement.GetProperty("client").GetString());
            Assert.Equal(4, first.RootElement.GetProperty("kills").GetInt32());
            Assert.Equal(2, first.RootElement.GetProperty("deaths").GetInt32());
            Assert.Equal(3, first.RootElement.GetProperty("ejects").GetInt32());
            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal(1, second.RootElement.GetProperty("kills").GetInt32());
            Assert.Equal(1, second.RootElement.GetProperty("deaths").GetInt32());
            Assert.Equal(1, second.RootElement.GetProperty("ejects").GetInt32());
            using var third = JsonDocument.Parse(lines[2]);
            Assert.Equal("guest", third.RootElement.GetProperty("kind").GetString());
            Assert.False(third.RootElement.GetProperty("launched").GetBoolean());
            Assert.Equal(JsonValueKind.Null, third.RootElement.GetProperty("sideCiv").ValueKind);
            using var summary = JsonDocument.Parse(lines[3]);
            Assert.Equal("match", summary.RootElement.GetProperty("type").GetString());
            Assert.Equal(2, summary.RootElement.GetProperty("humans").GetInt32());
            Assert.Equal(1, summary.RootElement.GetProperty("bots").GetInt32());
            Assert.Equal(4, lines.Length);
        }
        finally { Directory.Delete(dir, true); }
    }
}
