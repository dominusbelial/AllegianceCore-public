using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
[Trait("Tier", "Live")]
public sealed class P655ProbeWorkersLiveTests
{
    private sealed record Peer(string Name, short? Side, bool Ready);

    [SkippableFact]
    public async Task Probe_reports_side_refusal_instead_of_waiting_for_seat_timeout()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            Games = [new GameConfig { Name = "P655d refusal", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        server.Missions[0].LockSides = true;
        var startup = Task.Run(() => Record.Exception(() =>
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
        }));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!startup.IsCompleted && DateTime.UtcNow < until)
        {
            server.TickSim();
            await Task.Delay(5);
        }
        var error = Assert.IsType<InvalidOperationException>(await startup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("refused:", error.Message);
        Assert.DoesNotContain("never seated", error.Message);
        Assert.Empty(server.Ships);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_workers_two_seats_unique_probes_and_serves_MCP(bool authenticatedHarness)
    {
        Require.Artwork();
        string secret = Guid.NewGuid().ToString("N");
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        // Exercise the deployed JSON loader with the new limits OMITTED. A
        // directly constructed GameConfig concealed source-generator defaults.
        string configPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configPath, JsonSerializer.Serialize(new
            {
                artPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), staticCore = "PCore001.igc", port = 0,
                liveSettings = new
                {
                    testHarness = new { enabled = true, secret, money = 1000000, ignoreTechGate = true, characterNamePrefix = "mcp-" },
                    adminCheats = new { enabled = true, secret, allowNames = new[] { "mcp-*" } },
                },
                games = new[] { new { name = "P655d workers", map = "XXV_simple.igc", teams = 2, civIds = new[] { 1, 3 }, sideNames = new[] { "Alpha", "Bravo" } } },
            }));
            server.Boot(ServerConfigLoader.Load(configPath, []));
        }
        finally { File.Delete(configPath); }
        // Reproduce the started-mission balance gate without any harness privilege.
        // The authenticated case also verifies P485's normal-map ready-up.
        if (!authenticatedHarness) server.Missions[0].Stage = MissionStage.Started;
        var names = (Dictionary<ConnectionId, string>)typeof(Srv).GetField("_peerNames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var ships = (Dictionary<ConnectionId, Srv.LiveShip>)typeof(Srv).GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var ready = (Dictionary<ConnectionId, bool>)typeof(Srv).GetField("_playerReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        Peer[] snapshot = [];
        Exception? tickFailure = null;
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    server.TickSim();
                    Volatile.Write(ref snapshot, names.Select(n => new Peer(n.Value,
                        ships[n.Key].Ship.SideId,
                        ready.GetValueOrDefault(n.Key))).ToArray());
                    Thread.Sleep(50); // 20 UPS: keep the 30s auto-seat timeout beyond MCP's 15s deadline.
                }
            }
            catch (Exception e) { tickFailure = e; }
        }) { IsBackground = true };
        string work = Path.Combine(Path.GetTempPath(), "p655d-workers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = work, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { typeof(BotManager).Assembly.Location, "127.0.0.1", server.BoundPort.ToString(), "--workers", "2" }) info.ArgumentList.Add(arg);
        info.Environment["ALLEGIANCE_PRIVILEGE_SECRET"] = authenticatedHarness ? secret : "wrong-bearer";
        // Native lobby rank mode balances authenticated peers as well. Let
        // both probes auto-pick; the numeric bot exception does not override it.
        info.Environment.Remove("ALLEGIANCE_MCP_PROBE_SIDE_IDX");
        using var process = new Process { StartInfo = info };
        ticker.Start();
        Task<string>? stderr = null;
        bool processStarted = false;
        try
        {
            processStarted = process.Start();
            Assert.True(processStarted);
            stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            async Task<JsonElement> Rpc(int id, string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
                await process.StandardInput.FlushAsync();
                while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                    Assert.False(doc.RootElement.TryGetProperty("error", out _), line);
                    return doc.RootElement.GetProperty("result").Clone();
                }
                throw new IOException("MCP exited: " + await stderr);
            }
            await Rpc(1, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "p655d", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var hulls = await Rpc(2, "tools/call", new { name = "list_hulls", arguments = new { } });
            Assert.False(hulls.TryGetProperty("isError", out var error) && error.GetBoolean(), hulls.ToString());
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until && !(Volatile.Read(ref snapshot).Length == 2 && Volatile.Read(ref snapshot).All(p => p.Ready) && server.Missions[0].Stage == MissionStage.Started)) await Task.Delay(20);
            Assert.Null(tickFailure);
            var probes = Volatile.Read(ref snapshot);
            Assert.Equal(2, probes.Length);
            Assert.All(probes, p => { Assert.StartsWith("mcp-probe-w", p.Name); Assert.True(p.Ready); Assert.NotNull(p.Side); Assert.True(Srv.AdminNameMatches("mcp-*", p.Name)); });
            Assert.Equal(2, probes.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(MissionStage.Started, server.Missions[0].Stage);
            Assert.Equal(new short?[] { 0, 1 }, probes.Select(p => p.Side).Order());
            if (authenticatedHarness)
            {
                for (int i = 0; i < 2; i++)
                {
                    var spawn = await Rpc(3 + i, "tools/call", new { name = "spawn_bot", arguments = new { hull = "Adv Scout", side = 0, behavior = "idle" } });
                    Assert.False(spawn.TryGetProperty("isError", out error) && error.GetBoolean(), spawn.ToString());
                }
                var inventory = await Rpc(5, "tools/call", new { name = "list_active_bots", arguments = new { } });
                var bots = JsonDocument.Parse(inventory.GetProperty("content")[0].GetProperty("text").GetString()!);
                using (bots) Assert.Equal(2, bots.RootElement.GetArrayLength());
            }
        }
        finally
        {
            if (processStarted)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (stderr is not null) Console.WriteLine(await stderr);
            foreach (string log in Directory.GetFiles(work, "err.worker-*.log")) Console.WriteLine(await File.ReadAllTextAsync(log));
            stop.Cancel();
            Assert.True(ticker.Join(10000));
            Directory.Delete(work, recursive: true);
        }
    }
}
