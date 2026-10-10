using System.Diagnostics;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests;

// 'Allegiance.Server' is a namespace AND our type's namespace; the bare name
// 'Server' would bind to the namespace. Declared inside the namespace body so
// the using-alias reliably wins simple-name resolution.
using Server = global::Allegiance.Server.Server;
using Allegiance.TestInfra;
using Allegiance.Server;   // ServerConfig / GameConfig / ServerConfigLoader

/// <summary>
/// End-to-end tests of the headless server's boot + tick loop against the REAL
/// corpus (a real core and a real map), driving the logon handshake over the
/// real TCP socket with the real Wire codec. These are the "it starts, ticks,
/// and stops cleanly" acceptance proofs from the task.
/// </summary>
[Collection("LiveNetwork")]   // live-server tests: serialize against the other Thread.Sleep-paced live classes (P20 fix)
public class ServerTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static ServerConfig Config(int ups = 20, int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = ups,
        Games =
        [
            new GameConfig
            {
                Name = "Test Mission",
                Map = "XXV_simple.igc",
                Teams = 2,
                CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
            },
        ],
    };

    private static LogonRequest SampleRequest(string name = "E2E-BOT") => new()
    {
        CharacterName = name,
        ZoneTicket = new byte[] { 0x01, 0x02, 0x03, 0x04 },
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 555u,
        DwCookie = 0xBEEFu,
        CrcFileList = -1,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 70000000000000000ul,
        DrmHash = "e2e",
    };

    private static byte[] Pack(params IFedMessage[] msgs) =>
        FedPacket.Pack(msgs.Select(m => m.Encode()));

    /// <summary>
    /// The requirement "a test drives ~5 s of simulated time and asserts the tick
    /// count is within tolerance of 5 x 20". We run the REAL loop for ~5 s wall
    /// (which is ~5 s of sim time at 20 Hz) and assert LoopIterations is within a
    /// generous tolerance of 100. This also proves the loop ticks a started
    /// mission and that sim time advanced ~5 s.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FiveSecondsOfWallTime_YieldsAboutAHundredTicks_WithinTolerance()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config(ups: 20));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        long ticks = server.RunFor(TimeSpan.FromSeconds(5));

        int expected = 5 * 20; // 100
        Assert.InRange(ticks, expected - 30, expected + 30);
        Assert.InRange(server.SimNow, 4.2, 5.8);
        // every tick must have advanced a STARTED mission's clock
        var mission = server.Missions.Single();
        Assert.Equal(MissionStage.Started, mission.Stage);
        Assert.Equal(server.SimNow, mission.LastUpdate, precision: 6);
    }

    /// <summary>Fast deterministic cadence: 100 sim ticks advance exactly 100
    /// periods (5.0 s) of sim time — the cadence unit test, no wall-clock dependency.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SimTicks_AdvanceExactPeriodPerTick()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config(ups: 20));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        for (int i = 0; i < 100; i++) server.TickSim();

        Assert.Equal(100, server.LoopIterations);
        Assert.Equal(5.0, server.SimNow, precision: 6);
        Assert.Equal(0, server.Overruns); // sim path never books an overrun
    }

    /// <summary>
    /// The requirement "boots against a real core + real map" + "accepts TCP
    /// clients through the logon handshake": a real client transport connects,
    /// sends FM_C_LOGONREQ, and the server answers FM_S_LOGONACK with
    /// validation. Proves the whole chain: socket -> frame -> packet(CRC) ->
    /// LOGONREQ decode -> reply.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task ClientConnectsAndCompletesLogonHandshake()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config(ups: 20, port: 0)); // ephemeral port for the test
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => server.Loop(cts.Token));

        var client = new ClientHarness();
        try
        {
            client.Join(server.BoundPort, "HANDSHAKE-BOT");
            var ack = client.RequestLogon(SampleRequest("HANDSHAKE-BOT"));

            Assert.True(ack.FValidated, $"expected validated logon, got '{ack.CharNameOrFailureReason}'");
            Assert.Equal("Welcome HANDSHAKE-BOT", ack.CharNameOrFailureReason);
            // the server observed + logged the peer join (see Server.ConnectionsJoined)
            Assert.True(server.ConnectionsJoined >= 1, "server must have seen the peer join");
        }
        finally
        {
            client.Shutdown();
            cts.Cancel();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            server.Shutdown(); // clean teardown after the handshake
        }
    }

    /// <summary>The requirement "server survives a client connecting and
    /// disconnecting abruptly" — no exception, no death, the loop keeps ticking
    /// afterwards and a subsequent handshake still works.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task ServerSurvivesAbruptClientDisconnect()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config(ups: 20, port: 0));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => server.Loop(cts.Token));

        long ticksBefore;
        {
            using var client = new ClientHarness();
            client.Join(server.BoundPort, "FLAPPY");
            client.RequestLogon(SampleRequest("FLAPPY"));
            client.AbortTransport();   // close the socket without a clean handshake
            ticksBefore = server.LoopIterations;
        }

        // the server must keep ticking after the abrupt drop
        Thread.Sleep(300);
        Assert.True(server.LoopIterations > ticksBefore, "server must keep ticking after a drop");

        // and still serve a fresh client
        using var second = new ClientHarness();
        second.Join(server.BoundPort, "SECOND-BOT");
        var ack = second.RequestLogon(SampleRequest("SECOND-BOT"));
        Assert.True(ack.FValidated);

        cts.Cancel();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    /// <summary>The config fields are CLI-overridable and that flows through to
    /// boot (here: cadence). Exercises ServerConfigLoader.ApplyCli.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CliOverride_ChangesCadence()
    {
        Require.Artwork();
        var cfg = Config(ups: 20);
        var applied = ServerConfigLoader.ApplyCli(cfg, [("port", "64000"), ("ups", "50"), ("artpath", "/tmp")]);
        Assert.Equal(3, applied);
        Assert.Equal(50, cfg.UpdatesPerSecond);
        Assert.Equal(64000, cfg.Port);
        Assert.Equal("/tmp", cfg.ArtPath);
    }

    /// <summary>P185 — the config file parses identically through the
    /// source-generated JSON context as through the old reflection-based loader:
    /// camelCase keys bind case-insensitively, comments are skipped, and trailing
    /// commas are tolerated. Guards the exact behaviours ServerConfigLoader relied
    /// on before the switch to <c>ServerJsonContext</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Load_ParsesRealConfig_WithCommentsAndTrailingCommas()
    {
        Require.Artwork();
        string path = Path.Combine(Path.GetTempPath(), $"p185-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
        {
          // leading comment: tolerated via ReadCommentHandling.Skip
          "artPath": "/some/art",
          "staticCore": "PCore001.igc",
          "port": 6133,
          "updatesPerSecond": 25,
          "games": [
            {
              "name": "Parsed Mission",
              "map": "XXV_simple.igc",
              "teams": 2,
              "civIds": [ 1, 3 ],
              "sideNames": [ "Alpha", "Bravo" ],
            },
          ],
          "lobby": {
            "url": "http://127.0.0.1:9876",
            "displayName": "P185 Parse Test",
            "publicAddress": "10.9.9.9",
            "heartbeatIntervalSeconds": 13,
          },
        }
        """);

        try
        {
            var cfg = ServerConfigLoader.Load(path, []);
            Assert.Equal("/some/art", cfg.ArtPath);
            Assert.Equal("PCore001.igc", cfg.StaticCore);
            Assert.Equal(6133, cfg.Port);
            Assert.Equal(25, cfg.UpdatesPerSecond);

            var game = Assert.Single(cfg.Games);
            Assert.Equal("Parsed Mission", game.Name);
            Assert.Equal("XXV_simple.igc", game.Map);
            Assert.Equal(2, game.Teams);
            Assert.Equal([1, 3], game.CivIds);
            Assert.Equal(["Alpha", "Bravo"], game.SideNames);

            Assert.NotNull(cfg.Lobby);
            Assert.Equal("http://127.0.0.1:9876", cfg.Lobby!.Url);
            Assert.Equal("P185 Parse Test", cfg.Lobby.DisplayName);
            Assert.Equal("10.9.9.9", cfg.Lobby.PublicAddress);
            Assert.Equal(13, cfg.Lobby.HeartbeatIntervalSeconds);
        }
        finally { File.Delete(path); }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Boot_WithUnknownCivilization_ThrowsHelpfulError()
    {
        Require.Artwork();
        using var server = new Server();
        var cfg = Config();
        cfg.Games[0].GetType().GetProperty(nameof(GameConfig.CivIds))!
           .SetValue(cfg.Games[0], new int[] { 777, 778 }); // not in PCore001
        var ex = Assert.Throws<InvalidOperationException>(() => server.Boot(cfg));
        Assert.Contains("777", ex.Message);
        Assert.Contains("civilization", ex.Message);
    }

    /// <summary><c>--capture</c> off by default: leaving CapturePath null creates
    /// no file and changes nothing about the handshake.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Capture_is_off_by_default()
    {
        Require.Artwork();
        var path = Path.Combine(Path.GetTempPath(), $"nobody-{Guid.NewGuid():N}.cap");
        using var server = new Server();
        server.Boot(Config(ups: 20, port: 0)); // no CapturePath set
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => server.Loop(cts.Token));
        try
        {
            using var client = new ClientHarness();
            client.Join(server.BoundPort, "OFF-BOT");
            var ack = client.RequestLogon(SampleRequest("OFF-BOT"));
            Assert.True(ack.FValidated);
        }
        finally
        {
            cts.Cancel();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            server.Shutdown();
        }
        Assert.False(File.Exists(path), "no capture file should be created when --capture is absent");
    }

    /// <summary>With --capture set, every received and sent packet lands in the
    /// file and fedmsgdump's decoder can read it: both directions present, every
    /// packet's CRC verified, and the handshake is recognizable.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Capture_writes_both_directions_and_decodes()
    {
        Require.Artwork();
        var path = Path.Combine(Path.GetTempPath(), $"cap-{Guid.NewGuid():N}.cap");
        using var server = new Server();
        var cfg = Config(ups: 20, port: 0);
        cfg.CapturePath = path;
        server.Boot(cfg);
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => server.Loop(cts.Token));
        try
        {
            using var client = new ClientHarness();
            client.Join(server.BoundPort, "CAP-BOT");
            var ack = client.RequestLogon(SampleRequest("CAP-BOT"));
            Assert.True(ack.FValidated);
            // Let a SET_CLUSTER land too (the server sends it right after the ACK).
            Thread.Sleep(200);
        }
        finally
        {
            cts.Cancel();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            server.Shutdown(); // flushes + closes the capture file
        }

        var bytes = File.ReadAllBytes(path);
        var capture = CaptureStream.ReadAll(bytes);
        Assert.False(capture.Truncated, capture.Diagnostic ?? "");
        Assert.True(capture.Frames.Count >= 3, $"expected >= 3 frames (RX + TX ACK + TX SET_CLUSTER), got {capture.Frames.Count}");

        bool sawRecv = false, sawSend = false;
        int verified = 0;
        foreach (var fr in capture.Frames)
        {
            var dump = FedMessageDumper.DumpTyped(fr.Packet);
            if (dump.CrcVerified) verified++;
            if (fr.Direction == CaptureDirection.Receive) sawRecv = true; else sawSend = true;
        }
        Assert.True(sawRecv, "capture must contain a received packet (the LOGONREQ)");
        Assert.True(sawSend, "capture must contain sent packets (LOGONACK / SET_CLUSTER)");
        Assert.Equal(capture.Frames.Count, verified);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cli_recognizes_capture_flag()
    {
        Require.Artwork();
        var cfg = Config();
        int applied = ServerConfigLoader.ApplyCli(cfg, [("capture", "/tmp/handshake.cap")]);
        Assert.Equal(1, applied);
        Assert.Equal("/tmp/handshake.cap", cfg.CapturePath);
    }
}
