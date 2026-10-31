using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

public sealed class P901LanOptionsTests
{
    [Fact]
    public void Default_skirmish_network_and_dedicated_defaults_are_unchanged()
    {
        var local = new SkirmishOptions().CreateConfig();
        Assert.Equal("127.0.0.1", local.BindAddress);
        Assert.Equal(0, local.Port);
        Assert.False(local.Discovery!.Enabled);
        Assert.Equal("tcp", local.Transport!.Protocol);
        var dedicated = new Allegiance.Server.ServerConfig();
        Assert.Equal(6073, dedicated.Port);
        Assert.Null(dedicated.BindAddress);
        Assert.Null(dedicated.Discovery);
        Assert.Equal("", new Allegiance.Server.GameConfig().Password);
    }

    [Theory]
    [InlineData(SkirmishSize.Small)] [InlineData(SkirmishSize.Medium)]
    public void Lan_config_uses_both_transports_balanced_seats_and_no_offline_privileges(SkirmishSize size)
    {
        var config = new SkirmishOptions { HostLan = true, Size = size, GameName = "Friends", Password = "test-pass" }.CreateConfig();
        Assert.Equal("0.0.0.0", config.BindAddress);
        Assert.Equal(6073, config.Port);
        Assert.True(config.Discovery!.Enabled);
        Assert.Equal("udp", config.Transport!.Protocol); // DualTransport retains TCP.
        Assert.Equal(64, config.Transport.MaxConnectionsPerIp);
        Assert.Equal(128, config.Transport.MaxConnections);
        Assert.Equal(1, config.Games[0].MaxTeamImbalance);
        Assert.False(config.Games[0].NpcWingmanAmmoResupply);
        Assert.Equal("Friends", config.Games[0].Name);
        Assert.Equal("test-pass", config.Games[0].Password);
        Assert.False(config.LiveSettings.TestUnlock!.Enabled);
        Assert.False(config.LiveSettings.TestHarness!.Enabled);
        Assert.False(config.LiveSettings.TestHarness.IgnoreTechGate);
        Assert.Null(config.LiveSettings.AdminCheats);
        Assert.Null(config.BenchmarkSeed);
        Assert.False(config.LiveSettings.DebugMode);
        Assert.Null(config.LiveSettings.TimeScale);
        Assert.Equal(new Allegiance.Server.LiveSettingsConfig().TimeDilation, config.LiveSettings.TimeDilation);
        Assert.True(config.LiveSettings.AutoNextMatch.Enabled);
        Assert.Null(config.Lobby);
    }

    [Fact]
    public void Unsafe_overrides_are_refused_even_from_non_UI_callers()
    {
        var lan = new SkirmishOptions { HostLan = true };
        SkirmishOptions[] unsafeOptions =
        [
            lan with { TechMode = "unlocked" }, lan with { Seed = 1 }, lan with { Sizing = new() },
            lan with { StartCountdownSeconds = 1 }, lan with { ResearchSpeed = 2 },
            lan with { Speed = SkirmishSpeed.Showcase }, lan with { Speed = SkirmishSpeed.Fast },
            lan with { StartAsSpectator = true }, lan with { SpectatorDefault = true },
            lan with { Size = SkirmishSize.Grand }, lan with { HostPort = 0 }, lan with { HostPort = 65535 },
            lan with { GameName = "\n" }, lan with { Password = new string('a', 16) }
        ];
        foreach (var options in unsafeOptions) Assert.Throws<ArgumentException>(() => options.CreateConfig());
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Both_styles_use_the_same_host_setup_and_normal_join_navigation(HudStyle style)
    {
        var settings = new HudSettings { Style = style };
        Assert.Equal(style, settings.Style);
        Assert.Equal(IntroCommand.HostLan, IntroScreenModel.Activate(Array.IndexOf(IntroScreenModel.MainLabels, "Host LAN game")));
        var setup = new HostLanScreenModel();
        Assert.Equal(HostLanScreenModel.RowCount, setup.Rows.Length);
        setup.Type('!');
        Assert.Equal("LAN game!", setup.CreateOptions().GameName);
        setup.Move(1);
        for (int i = 0; i < 4; i++) setup.Backspace();
        foreach (char c in "17073") setup.Type(c);
        setup.Move(1); foreach (char c in "test") setup.Type(c);
        Assert.DoesNotContain("test", setup.Rows[2]);
        Assert.Contains("****", setup.Rows[2]);
        Assert.Equal(17073, setup.CreateOptions().HostPort);
        Assert.Equal("test", setup.CreateOptions().Password);
        Assert.Contains("Firewall", HostLanScreenModel.NetworkHelp);
        Assert.Contains("Tailscale", HostLanScreenModel.InternetHelp);
        Assert.True(IntroScreenModel.TryParseDirect("100.64.0.1:17073", 6073, out string host, out ushort port));
        Assert.Equal("100.64.0.1", host); Assert.Equal(17073, port);
    }
}

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P901LanLiveTests(ITestOutputHelper output)
{
    static int FreePort()
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            if (port >= 65534) continue;
            try
            {
                using var udp = new UdpClient(port);
                using var discovery = new UdpClient(port + 1);
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException("No free game/discovery port pair");
    }
    static async Task Until(Func<bool> predicate, int seconds = 90)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed.TotalSeconds < seconds) await Task.Delay(50);
        Assert.True(predicate(), "LAN proof timed out");
    }

    [SkippableTheory]
    [InlineData("tcp")] [InlineData("udp")] [InlineData("discovery")]
    public async Task Taken_ports_fail_clearly_and_release_partial_listeners(string kind)
    {
        Require.Artwork();
        int port = FreePort();
        using var tcp = kind == "tcp" ? new TcpListener(IPAddress.Any, port) : null;
        tcp?.Start();
        using var udp = kind == "tcp" ? null : new UdpClient(kind == "udp" ? port : port + 1);
        var error = await Assert.ThrowsAsync<IOException>(() => SkirmishHost.StartAsync(new() { HostLan = true, HostPort = port }));
        Assert.Contains("port", error.Message, StringComparison.OrdinalIgnoreCase);
        if (kind != "tcp") { using var retry = new TcpListener(IPAddress.Any, port); retry.Start(); }
    }

    [SkippableFact]
    public async Task Separate_guests_discover_and_join_TCP_UDP_drop_rejoin_rotate_and_receive_host_shutdown()
    {
        Require.Artwork();
        using var host = await SkirmishHost.StartAsync(new() { HostLan = true, HostPort = FreePort(), GameName = "P901 friends", Password = "lan-test",
            Goals = new MissionGoals { GameLengthSeconds = 45 } });
        ushort port = host.Port;
        int[] pids = host.BotProcessIds;
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetPausedAsync(true));
        using var pilot = new ConnectionSession();
        pilot.Connect(port, "LAN host pilot");
        pilot.SendLogonRequest(new() { CharacterName = "LAN host pilot", FedsrvVer = LogonRequest.MsgVer,
            DwCookie = uint.MaxValue, CrcFileList = -1, MissionPassword = "lan-test", Time = 1 });
        Assert.True(pilot.WaitForAck(15000).FValidated);
        pilot.SendPositionReq(0);
        await Until(() => { lock (pilot.Gate) return pilot.World.MyShipId is not null; });
        pilot.SendPlayerReady(true, pilot.World.MyShipId!.Value);
        await Until(() => host.OnSimulation(s => s.Missions[0].Stage == MissionStage.Started), 150);
        using var a = Guest.Start(port, "direct", "tcp", "P901 guest A", 1);
        using var b = Guest.Start(port, "discovery", "udp", "mcp-p901-guest B", 0);
        using var c = Guest.Start(port, "discovery", "tcp", "P901 guest C", 1);
        using var d = Guest.Start(port, "direct", "udp", "P901 guest D", 0);
        foreach (var guest in new[] { a, b, c, d }) await guest.WaitSeated();
        await a.WaitFlying(); await b.WaitFlying();
        output.WriteLine("Separate processes: TCP/direct, UDP/discovery, TCP/discovery, UDP/direct all seated.");
        Assert.Contains("P901 friends", host.LanStatus);
        var counts = host.OnSimulation(s => s.Ships.GroupBy(x => x.Ship.SideId).Select(g => g.Count()).ToArray());
        Assert.Equal(2, counts.Length); Assert.InRange(Math.Abs(counts[0] - counts[1]), 0, 1);
        foreach (var guest in new[] { a, b }) await guest.Send("cheat");
        await a.Send("finale " + host.OnSimulation(s => s.Missions[0].ResearchEpoch));
        await Task.Delay(500);
        Assert.False(host.OnSimulation(s => ((TimeDilationController)typeof(Allegiance.Server.Server)
            .GetField("_tidiController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(s)!).ManualOverride));
        Assert.Equal(0, host.OnSimulation(s => ((System.Collections.ICollection)typeof(Allegiance.Server.Server)
            .GetField("_researchFinales", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(s)!).Count));
        Assert.All(host.OnSimulation(s => s.Ships.Select(x => x.Wallet.Money).ToArray()), money => Assert.True(money < 1_000_000));
        using (var wrong = Guest.Start(port, "direct", "udp", "wrong password", 1, "bad"))
        {
            var status = await wrong.WaitRefused("BadPassword");
            Assert.Equal(JsonValueKind.Null, status.GetProperty("ship").ValueKind);
            Assert.Equal("BadPassword", status.GetProperty("refusal").GetString());
        }
        await d.Send("quit"); await d.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Until(() => host.OnSimulation(s => s.Ships.All(x => x.Ship.Name != "P901 guest D")));
        using var rejoined = Guest.Start(port, "direct", "udp", "P901 guest D", 0);
        await rejoined.WaitSeated();
        long generation = (await a.Status()).GetProperty("generation").GetInt64();
        // A real native countdown goal ends this played match; no admin or forced game-over seam.
        var firstMatch = host.OnSimulation(s => s.Missions[0]);
        await Until(() => host.OnSimulation(s => !ReferenceEquals(firstMatch, s.Missions[0])), 90);
        await a.WaitGameOver();
        await Until(() => host.OnSimulation(s => s.Missions[0].Stage == MissionStage.Started), 180);
        foreach (var guest in new[] { a, b, c, rejoined })
        {
            await guest.WaitSeated();
            Assert.True((await guest.Status()).GetProperty("generation").GetInt64() > generation);
        }
        Assert.True(host.BotsAlive);
        host.Dispose();
        foreach (var guest in new[] { a, b, c, rejoined })
        {
            try { await guest.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { throw new IOException("Guest failed to exit: " + string.Join("\n", guest.Lines)); }
            await guest.DrainOutput();
            Assert.Contains("LOST ServerShutdown", guest.Lines);
            Assert.Equal(0, guest.Process.ExitCode);
        }
        await Until(() => pids.All(Exited));
        using var tcp = new TcpListener(IPAddress.Any, port); tcp.Start();
        using var udp = new UdpClient(port); using var discovery = new UdpClient(port + 1);
        output.WriteLine("Next match migrated all guests; shutdown reason delivered; all owned processes exited; TCP, UDP and discovery ports rebound.");
    }

    static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    sealed class Guest : IDisposable
    {
        public Process Process { get; }
        public readonly List<string> Lines = [];
        readonly Task reader, errors;
        readonly System.Threading.Channels.Channel<JsonElement> statuses = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        Guest(Process process)
        {
            Process = process;
            reader = Task.Run(async () =>
            {
                while (await Process.StandardOutput.ReadLineAsync() is { } line)
                {
                    lock (Lines) Lines.Add(line);
                    if (line.StartsWith("STATUS ")) statuses.Writer.TryWrite(JsonDocument.Parse(line[7..]).RootElement.Clone());
                }
            });
            errors = Task.Run(async () => { string error = await Process.StandardError.ReadToEndAsync(); lock (Lines) Lines.Add(error); });
        }
        public static Guest Start(ushort port, string source, string protocol, string name, short side, string password = "lan-test")
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in new[] { Path.Combine(root, "tools/p901-lan-proof/bin", configuration, "net10.0/p901-lan-proof.dll"), "guest", "127.0.0.1", port.ToString(), source, protocol, name, side.ToString(), password })
                start.ArgumentList.Add(arg);
            return new(Process.Start(start)!);
        }
        public Task DrainOutput() => Task.WhenAll(reader, errors).WaitAsync(TimeSpan.FromSeconds(5));
        public async Task Send(string command) { await Process.StandardInput.WriteLineAsync(command); await Process.StandardInput.FlushAsync(); }
        public async Task<JsonElement> Status()
        {
            await Send("status");
            return await statuses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        }
        public async Task WaitGameOver()
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 15)
            {
                if ((await Status()).GetProperty("overSeen").GetBoolean()) return;
                await Task.Delay(100);
            }
            throw new IOException("Guest did not receive game over");
        }
        public async Task<JsonElement> WaitRefused(string reason)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 15)
            {
                var status = await Status();
                if (status.GetProperty("refusal").GetString() == reason) return status;
                await Task.Delay(100);
            }
            throw new IOException("Guest refusal not received: " + string.Join("\n", Lines));
        }
        public async Task WaitFlying()
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 30)
            {
                var status = await Status();
                if (status.GetProperty("flying").GetBoolean() && status.GetProperty("distance").GetDouble() > 1) return;
                await Task.Delay(100);
            }
            throw new IOException("Guest did not fly: " + string.Join("\n", Lines));
        }
        public async Task WaitSeated()
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 30)
            {
                var status = await Status();
                if (status.GetProperty("ship").ValueKind != JsonValueKind.Null) return;
                await Task.Delay(100);
            }
            throw new IOException("Guest not seated: " + string.Join("\n", Lines));
        }
        public void Dispose()
        {
            if (!Process.HasExited) { Process.Kill(entireProcessTree: true); Process.WaitForExit(); }
            Task.WaitAll([reader, errors], 5000);
            Process.Dispose();
        }
    }
}
