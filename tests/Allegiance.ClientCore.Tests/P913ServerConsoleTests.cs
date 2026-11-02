using System.Diagnostics;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P913ServerConsoleTests(ITestOutputHelper output)
{
    private const string Moderator = "p913-moderator-bearer";
    private const string CheatSecret = "p913-cheat-bearer";
    private static ServerConfig Config() => new()
    {
        Port = 0,
        Transport = new() { Mode = "pool" },
        ModeratorAccounts = [Moderator],
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
        Sim = new() { NativeGotoPlan = true },
        LiveSettings = new() { AdminCheats = new() { Enabled = true, Secret = CheatSecret, AllowNames = ["admin"] } },
        Games = [
            new() { Name = "P913-A", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new() { Name = "P913-B", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static void Pump(Srv server, Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(8)) { server.TickSim(); Thread.Sleep(2); }
        Assert.True(done(), "loopback condition timed out");
    }

    private static bool Read(ConnectionSession peer, Func<bool> read) { lock (peer.Gate) return read(); }
    private static bool HasChat(ConnectionSession peer, string text) => Read(peer, () => peer.World.Events.Chats.Any(c => c.Message == text));
    private static short Id(ConnectionSession peer) => peer.Ack!.ShipID;

    private static ConnectionSession Connect(Srv server, string name, int game = 0, bool seat = true)
    {
        var peer = new ConnectionSession();
        try
        {
            peer.Connect(server.BoundPort, name);
            peer.SendLogonRequest(new()
            {
                CharacterName = name, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                CDKey = Moderator, ZoneTicket = [1, 2, 3], DwCookie = server.Missions[game].Cookie,
                SteamAuthTicket = new byte[16],
            });
            Pump(server, () => peer.Ack is not null);
            Assert.True(peer.Ack!.FValidated);
            if (seat)
            {
                peer.SendPositionReq(0);
                Pump(server, () => Read(peer, () => peer.World.MySideId == 0) && server.Ships.Any(s => s.Ship.Id == Id(peer)));
            }
            return peer;
        }
        catch { peer.Dispose(); throw; }
    }

    private static string Command(Srv server, StringWriter log, string text)
    {
        log.GetStringBuilder().Clear();
        // This is the production stdin reader, including EOF behavior. It queues
        // only; no mutation/output can happen before the next simulation pass.
        server.ReadConsoleInput(new StringReader(text + "\n"), CancellationToken.None);
        Assert.Equal("", log.ToString());
        server.TickSim();
        return log.ToString();
    }

    private static void Capture(Action<Srv, StringWriter> test)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(log));
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(Config());
            test(server, log);
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableFact]
    public void Stdin_roster_status_and_boot_follow_live_membership_including_side_select()
    {
        Capture((server, log) =>
        {
            using var a = Connect(server, "Pilot");
            using var b = Connect(server, "Waiting", 1, seat: false);
            short aId = Id(a), bId = Id(b);
            string players = Command(server, log, "players");
            Assert.Contains($"id={Id(a)}", players);
            Assert.Contains("name='Pilot' game='P913-A' team='Alpha'", players);
            Assert.Contains($"id={Id(b)}", players);
            Assert.Contains("name='Waiting' game='P913-B' team='lobby' sector='' state=side-select", players);
            long packets = server.Counters.CPacketsOut;
            string status = Command(server, log, "status");
            Assert.Contains("connections=2 players=2 seated=1 missions=2", status);
            Assert.Contains($"packetsOut={packets}", status);
            Assert.Contains("game='P913-A' stage=NotStarted", status);
            Assert.Contains("uptime=", status);
            Command(server, log, $"boot {aId}");
            Assert.DoesNotContain(server.Ships, s => s.Ship.Id == aId);
            Pump(server, () => Read(a, () => a.World.OwnMissionDeparture is { Reason: QuitSideReason.AdminBooted }));
            Assert.DoesNotContain("name='Pilot'", Command(server, log, "players"));
            Assert.Contains("players=1 seated=0", Command(server, log, "status"));
            Command(server, log, $"boot {bId}");
            Assert.Contains("players=0", Command(server, log, "players"));
            Assert.Contains("players=0 seated=0", Command(server, log, "status"));
        });
    }

    [SkippableFact]
    public void Say_reaches_all_games_whisper_only_selected_user_and_history_never_sends_to_clients()
    {
        Capture((server, log) =>
        {
            using var a = Connect(server, "AlphaPilot");
            using var b = Connect(server, "BravoPilot", 1);
            using var waiting = Connect(server, "LobbyPilot", 1, seat: false);
            Command(server, log, "say All games hello");
            Pump(server, () => HasChat(a, "All games hello") && HasChat(b, "All games hello") && HasChat(waiting, "All games hello"));
            lock (a.Gate)
                Assert.Contains(a.World.Events.Chats, c => c.Message == "All games hello" && c.SidSender == -1
                    && c.OidRecipient == -1 && c.CommandID == OrderChange.CidNone && c.BObjectModel
                    && c.OtTarget == -1 && c.OidTarget == -1 && c.VoiceOver == -1);
            Command(server, log, $"whisper {Id(b)} Private hello");
            Pump(server, () => HasChat(b, "Private hello"));
            Assert.False(HasChat(a, "Private hello"));
            Assert.False(HasChat(waiting, "Private hello"));
            string longMessage = new('L', 400);
            Command(server, log, "say " + longMessage);
            Pump(server, () => HasChat(a, longMessage) && HasChat(b, longMessage));
            Command(server, log, $"whisper {Id(waiting)} Pending hello");
            Pump(server, () => HasChat(waiting, "Pending hello"));
            Assert.False(HasChat(b, "Pending hello"));
            lock (b.Gate)
                Assert.Contains(b.World.Events.Chats, c => c.Message == "Private hello" && c.SidSender == -1 && c.ChatTarget == (int)ChatTarget.Individual && c.CommandID == OrderChange.CidNone
                    && c.BObjectModel && c.OtTarget == -1 && c.OidTarget == -1 && c.VoiceOver == -1);
            Command(server, log, "chat on");
            a.SendChat("real player chat", ChatTarget.Everyone);
            Pump(server, () => HasChat(a, "real player chat"));
            Assert.False(HasChat(b, "real player chat"));
            var before = new[] { Count(a), Count(b), Count(waiting) };
            Assert.Contains("message='real player chat'", Command(server, log, "chat 20"));
            Command(server, log, "players");
            Command(server, log, "status");
            Command(server, log, "alerts off");
            server.TickSim(); Thread.Sleep(30);
            Assert.Equal(before, new[] { Count(a), Count(b), Count(waiting) });
        });
    }

    private static int Count(ConnectionSession peer) { lock (peer.Gate) return peer.World.Events.Chats.Count; }

    [SkippableFact]
    public void Ring_wraps_preferences_gate_capture_and_native_admin_chat_triggers_alert_only_on_server()
    {
        Capture((server, log) =>
        {
            using var a = Connect(server, "HelpCaller");
            using var b = Connect(server, "OtherPilot");
            Assert.Contains("retained=0", Command(server, log, "chat"));
            Command(server, log, "chat on");
            // say records one entry for each hosted mission; overflow the fixed ring.
            for (int i = 0; i < Srv.ConsoleChatCapacity / 2 + 3; i++) Command(server, log, $"say history-{i:D3}");
            string history = Command(server, log, $"chat {Srv.ConsoleChatCapacity}");
            Assert.Contains($"retained={Srv.ConsoleChatCapacity}", history);
            Assert.DoesNotContain("message='history-000'", history);
            Assert.Contains("message='history-102'", history);
            Assert.Equal(Srv.ConsoleChatCapacity, history.Split('\n').Count(l => l.StartsWith("[admin-chat]")));
            Command(server, log, "chat off");
            Command(server, log, "say unrecorded");
            Assert.DoesNotContain("message='unrecorded'", Command(server, log, "chat 200"));
            log.GetStringBuilder().Clear();
            a.SendChat("Need admin help", ChatTarget.Admin);
            Pump(server, () => log.ToString().Contains("[admin-alert]"));
            Assert.Contains("target=Admin", log.ToString());
            Assert.Contains("sender='HelpCaller'", log.ToString());
            Assert.False(HasChat(a, "Need admin help"));
            Assert.False(HasChat(b, "Need admin help"));
            Command(server, log, "alerts off");
            a.SendChat("Quiet admin help", ChatTarget.Admin);
            Pump(server, () => log.ToString().Contains("Quiet admin help")); // ordinary dispatch diagnostic, not an alert
            Assert.DoesNotContain("[admin-alert]", log.ToString());
            Assert.DoesNotContain("Need admin help", Command(server, log, "chat 200"));
            Command(server, log, "chat on");
            a.SendChat("History without popup", ChatTarget.Admin);
            Pump(server, () => log.ToString().Contains("message='History without popup'"));
            Assert.DoesNotContain("[admin-alert]", log.ToString());
            Assert.Contains("message='History without popup'", Command(server, log, "chat"));
            Command(server, log, "alerts on");
            a.SendChat("Alert restored", ChatTarget.Admin);
            Pump(server, () => log.ToString().Contains("[admin-alert]"));
        });
    }

    [SkippableFact]
    public void Secrets_controls_malformed_input_EOF_and_queue_bounds_are_safe()
    {
        Capture((server, log) =>
        {
            using var a = Connect(server, "UntrustedPilot");
            using var secretName = Connect(server, CheatSecret);
            Assert.DoesNotContain(Moderator, log.ToString()); // CDKey never enters the view
            Assert.DoesNotContain(CheatSecret, Command(server, log, "players"));
            secretName.Dispose();
            Pump(server, () => server.Ships.Count == 1);
            Assert.DoesNotContain(CheatSecret, log.ToString()); // departure diagnostics too
            Command(server, log, "chat on");
            a.SendChat($"bad\u001b[31m\nline {Moderator} {CheatSecret}", ChatTarget.Admin);
            Pump(server, () => log.ToString().Contains("[admin-alert]"));
            string text = log.ToString();
            Assert.DoesNotContain(Moderator, text);
            Assert.DoesNotContain(CheatSecret, text);
            Assert.DoesNotContain('\u001b', text);
            Assert.Contains("[redacted]", text);
            var entry = text.Split('\n').Single(l => l.StartsWith("[admin-alert]"));
            Assert.Contains("bad[31mline", entry);
            Assert.DoesNotContain(CheatSecret, Command(server, log, $"say {CheatSecret}"));
            foreach (string malformed in new[] { "boot", "boot -1", "boot 999999999", "players extra", "whisper 1", "whisper nope hi", "status extra", "chat -1", "chat 0", "chat 201", "alerts maybe", "skills extra", Moderator, new string('x', 2000) })
            {
                string response = Command(server, log, malformed);
                Assert.Single(response.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                Assert.StartsWith("usage:", response);
                Assert.DoesNotContain(Moderator, response);
            }
            Assert.Contains("no player id=32767", Command(server, log, "boot 32767"));
            Assert.Contains("usage:", Command(server, log, "help"));
            Assert.Contains("[skills]", Command(server, log, "skills"));
            log.GetStringBuilder().Clear();
            server.ReadConsoleInput(new StringReader(""), CancellationToken.None);
            server.TickSim();
            Assert.DoesNotContain("usage:", log.ToString());
            // A bounded stdin backlog cannot monopolize a tick or wait for a tick.
            for (int i = 0; i < 64; i++) Assert.True(server.RequestConsoleCommand("help"));
            Assert.False(server.RequestConsoleCommand("help"));
            log.GetStringBuilder().Clear();
            server.TickSim();
            Assert.Equal(8, log.ToString().Split('\n').Count(l => l.StartsWith("usage:")));
        });
    }

    [SkippableFact]
    public void Disabled_observation_at_394_peers_allocates_nothing_and_never_enumerates_roster()
    {
        Capture((server, log) =>
        {
            // Real loopback joins are exercised above. This isolated hook proof
            // reuses the existing 394-peer broadcast fixture to measure only the
            // added disabled observer, independent of network/sim allocations.
            var mission = server.Missions[0];
            using var a = Connect(server, "AllocationPilot");
            var live = server.Ships.Single();
            var peers = Enumerable.Range(1, 394).Select(i => (new Allegiance.Core.Transport.ConnectionId((uint)i), live.Ship, live.Sector)).ToArray();
            server.TestSeedPeers(peers);
            var chat = new ChatMessage { Message = "disabled ordinary chat", ChatTarget = (int)ChatTarget.Everyone };
            server.ObserveConsoleChat(mission, "sender", chat); // JIT warmup
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 394; i++) server.ObserveConsoleChat(mission, "sender", chat);
            long elapsed = Stopwatch.GetTimestamp() - start;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"394 peers / 394 disabled observations: {allocated} bytes, {elapsed * 1e9 / Stopwatch.Frequency / 394:F1} ns/call");
            Assert.Equal(0, allocated);
            Assert.Contains("retained=0", Command(server, log, "chat"));
            server.TestResetBroadcastState();
        });
    }
}
