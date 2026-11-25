using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P1085BackfillRecoveryLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Exhausted_probe_and_combat_sessions_are_replaced_and_both_sides_refill_after_outage()
    {
        Require.Artwork();
        using var rig = new P1072WebSocketBotsLiveTests.Rig();
        await using var link = new FaultLink(rig.Server.BoundPort);
        using var manager = new BotManager("localhost", link.Port, backfill: true, transport: new());
        using var stop = new CancellationTokenSource();
        Task driving = new BackfillDriver(manager, 2).RunAsync(stop.Token);
        try
        {
            await Until(() => Full(manager), driving);
            var original = manager.TransportClients;
            var probe = manager.Probe;
            Assert.Equal(4, original.Length);
            // Arm the real owned reconnect window without changing process-wide
            // environment. The link rejects ALL dials, including owned resumes.
            foreach (var (name, peer) in original)
                peer.EnableAutoReconnect("localhost", link.Port, name, 1);
            double outageAt = link.Now;
            link.Offline = true;
            link.DropAll();
            await Until(() => original.All(p => p.Session.Lost), driving);
            Assert.All(original, p => Assert.True(p.Session.ResumeAttempts > 0));
            // Cross three backoff steps while fresh probe/commander dials fail.
            await Until(() => FreshAttempts("mcp-probe").Length >= 3 && FreshAttempts("mcp-cmd").Length >= 3, driving);
            Assert.Same(probe, manager.Probe);
            Assert.False(manager.BackfillRoster(BotNameRules.Defaults()).Ready);
            foreach (string prefix in new[] { "mcp-probe", "mcp-cmd" })
            {
                var attempts = FreshAttempts(prefix);
                Assert.InRange(attempts.Length, 3, 6);
                for (int i = 1; i < attempts.Length; i++)
                    Assert.True(attempts[i].At - attempts[i - 1].At >= Math.Pow(2, Math.Min(i - 1, 5)) - .05,
                        "new-session dials must respect exponential backoff");
                output.WriteLine(prefix + " fresh outage attempts at seconds: " + string.Join(", ", attempts.Select(a => (a.At - outageAt).ToString("F2"))));
            }
            link.Offline = false;
            await Until(() => !ReferenceEquals(probe, manager.Probe) && Full(manager), driving, 60);
            Assert.True(probe.IsDisposed);
            Assert.All(original, p => Assert.True(p.Session.IsDisposed));
            Assert.Equal(2, manager.TransportClients.Count(p => p.Name.StartsWith("mcp-b", StringComparison.Ordinal)));
            Assert.Equal(4, manager.TransportClients.Length);
            AssertReadyProbeAndCommanderRegistration(manager);
            output.WriteLine("restored roster: " + JsonSerializer.Serialize(manager.BackfillRoster(BotNameRules.Defaults()).Pilots));
            Assert.Equal(0, rig.Server.SimPassStageFailures);

            (string Name, double At)[] FreshAttempts(string prefix) => link.Greetings
                .Where(p => p.At >= outageAt && p.Name.StartsWith(prefix, StringComparison.Ordinal)
                    && original.All(o => o.Name != p.Name)).ToArray();
        }
        finally { stop.Cancel(); try { await driving; } catch (OperationCanceledException) { } }
    }

    [SkippableFact]
    public async Task Never_validated_startup_and_disposed_probe_recover_with_original_side_preference_and_ready_up()
    {
        Require.Artwork();
        string? saved = Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_PROBE_SIDE_IDX");
        Environment.SetEnvironmentVariable("ALLEGIANCE_MCP_PROBE_SIDE_IDX", "1");
        try
        {
            using var rig = new P1072WebSocketBotsLiveTests.Rig();
            await using var link = new FaultLink(rig.Server.BoundPort) { Offline = true };
            using var manager = new BotManager("localhost", link.Port, backfill: true, transport: new());
            var unvalidated = manager.Probe;
            Assert.Null(unvalidated.Ack);
            using var stop = new CancellationTokenSource();
            Task driving = new BackfillDriver(manager, 2).RunAsync(stop.Token);
            try
            {
                link.Offline = false;
                await Until(() => Full(manager), driving);
                Assert.NotSame(unvalidated, manager.Probe);
                Assert.True(unvalidated.IsDisposed);
                Assert.Equal((short)1, manager.ProbeSide);
                AssertReadyProbeAndCommanderRegistration(manager);
                // Voluntary departure clears the old seat before disposal,
                // exercising the disposed-session gate independently of Lost.
                var old = manager.Probe;
                link.Offline = true;
                old.SendLogoff();
                await rig.Until(() => { lock (rig.Gate) return rig.Server.Ships.Count == 3; });
                old.Dispose();
                link.Offline = false;
                await Until(() => !ReferenceEquals(old, manager.Probe) && Full(manager), driving);
                Assert.Equal((short)1, manager.ProbeSide);
                AssertReadyProbeAndCommanderRegistration(manager);
                Assert.Equal(4, manager.TransportClients.Length);
                Assert.Equal(0, rig.Server.SimPassStageFailures);
                output.WriteLine("initial unvalidated probe and later disposed probe both replaced; forced roster side 1 and PLAYER_READY retained");
            }
            finally { stop.Cancel(); try { await driving; } catch (OperationCanceledException) { } }
        }
        finally { Environment.SetEnvironmentVariable("ALLEGIANCE_MCP_PROBE_SIDE_IDX", saved); }
    }

    private static bool Full(BotManager manager)
    {
        var roster = manager.BackfillRoster(BotNameRules.Defaults());
        var probe = manager.Probe;
        lock (probe.Gate)
            if (probe.World.MyShipId is not { } id || !probe.World.Roster.ReadyByShipId.GetValueOrDefault(id)) return false;
        return roster.Ready && new short[] { 0, 1 }.All(s => roster.Pilots.Count(p => p.Side == s) == 2)
            && roster.Pilots.Count(p => p.Owned) == 2;
    }

    private static void AssertReadyProbeAndCommanderRegistration(BotManager manager)
    {
        var commanders = P1072WebSocketBotsLiveTests.Field<Dictionary<short, Allegiance.ClientCore.Session.ConnectionSession>>(manager, "_commanders");
        lock (commanders)
        {
            Assert.Equal(2, commanders.Count);
            Assert.Same(manager.Probe, commanders[manager.ProbeSide]);
        }
        lock (manager.Probe.Gate)
        {
            Assert.True(manager.Probe.World.Roster.ReadyByShipId[manager.Probe.World.MyShipId!.Value]);
        }
    }

    private static async Task Until(Func<bool> done, Task driving, double seconds = 40)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed.TotalSeconds < seconds)
        {
            Assert.False(driving.IsCompleted, driving.Exception?.ToString());
            await Task.Delay(50);
        }
        Assert.True(done(), "P1085 loopback recovery deadline exceeded");
    }

    /// <summary>Real TCP forwarding with an explicit link outage. Only this
    /// fixture's sockets are closed; server and driver continue running.</summary>
    private sealed class FaultLink : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
        private readonly ConcurrentBag<Task> _connections = new();
        private readonly ushort _target;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Task _accept;
        internal volatile bool Offline;
        internal double Now => _clock.Elapsed.TotalSeconds;
        internal readonly ConcurrentQueue<(string Name, double At)> Greetings = new();
        internal ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
        internal FaultLink(ushort target) { _target = target; _listener.Start(); _accept = Accept(); }
        private async Task Accept()
        {
            try { while (!_stop.IsCancellationRequested) _connections.Add(Forward(await _listener.AcceptTcpClientAsync(_stop.Token))); }
            catch (OperationCanceledException) { }
        }
        private async Task Forward(TcpClient client)
        {
            using (client)
            using (var upstream = new TcpClient())
            try
            {
                _clients[client] = 0;
                client.NoDelay = upstream.NoDelay = true;
                byte[] header = new byte[5];
                await client.GetStream().ReadExactlyAsync(header, _stop.Token);
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1)));
                if (length is < 1 or > 4096) throw new InvalidDataException("invalid fixture greeting");
                byte[] body = new byte[length];
                await client.GetStream().ReadExactlyAsync(body, _stop.Token);
                Greetings.Enqueue((Encoding.UTF8.GetString(body), Now));
                if (Offline) return;
                await upstream.ConnectAsync(IPAddress.Loopback, _target, _stop.Token);
                await upstream.GetStream().WriteAsync(header, _stop.Token);
                await upstream.GetStream().WriteAsync(body, _stop.Token);
                Task outbound = client.GetStream().CopyToAsync(upstream.GetStream(), _stop.Token);
                Task inbound = upstream.GetStream().CopyToAsync(client.GetStream(), _stop.Token);
                await Task.WhenAny(outbound, inbound);
                client.Dispose(); upstream.Dispose();
                try { await Task.WhenAll(outbound, inbound); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            finally { _clients.TryRemove(client, out _); }
        }
        internal void DropAll() { foreach (var client in _clients.Keys) client.Dispose(); }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); DropAll();
            await _accept;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
