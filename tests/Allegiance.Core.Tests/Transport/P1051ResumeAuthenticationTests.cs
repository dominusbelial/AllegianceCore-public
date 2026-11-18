using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
public sealed class P1051ResumeAuthenticationTests
{
    [SkippableFact]
    public async Task Valid_WS_resume_retires_both_prelogon_deadlines_and_restores_human_classification()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var websocket = P1048WebSocketTests.Settings(); websocket.AuthenticationTimeoutSeconds = 1.5;
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, BindAddress = "127.0.0.1",
            UpdatesPerSecond = 20, Discovery = new() { Enabled = false }, WebSocket = websocket,
            Transport = new() { PreLogonTimeoutSeconds = 1 }, Reconnect = new() { Enabled = true, GraceSeconds = 10 },
            Games = [new() { Name = "P1051 resume", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"], AutoStart = true, AllowEmptyTeams = true, StartCountdownSeconds = 0 }],
        });
        await using var session = new ConnectionSession
        {
            WebSocketEndpoint = WebSocketEndpoint.FromConfiguration($"ws://127.0.0.1:{server.WebSocketPort}/game"),
            WebSocketOrigin = "http://localhost",
        };
        session.EnableAutoReconnect();
        var join = session.JoinAsync(server.BoundPort, "Pilot1051", new LogonRequest
        { CharacterName = "Pilot1051", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1, DwCookie = uint.MaxValue, Time = 1 });
        await Pump(() => join.IsCompleted); Assert.Null(await join);
        session.SendPositionReq(0); await Pump(() => server.Ships.Count == 1 && session.World.MyShipId is not null);
        short shipId = session.World.MyShipId!.Value;
        session.SendPlayerReady(true, shipId); await Pump(() => session.World.Mission?.Stage == MissionStageMessage.StageStarted);
        session.SendDocked(); await Pump(() => session.World.Ships[shipId].StationId < 0);

        var names = (Dictionary<ConnectionId, string>)typeof(Srv).GetField("_peerNames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var initialPeer = names.Single(pair => pair.Value == "Pilot1051").Key;
        Assert.True(server.Host.Transport!.IsPeerHuman(initialPeer));
        server.Host.Transport.ClosePeer(initialPeer, DisconnectReason.ReadErrorOrEof);
        await Pump(() => session.ResumeSuccesses == 1 && !session.Reconnecting);
        var resumedPeer = names.Single(pair => pair.Value == "Pilot1051").Key;
        Assert.NotEqual(initialPeer, resumedPeer);
        Assert.True(server.Host.Transport.IsPeerHuman(resumedPeer));
        Assert.Equal(0, server.PreLogonPendingCount);
        Assert.Equal(shipId, session.World.MyShipId);
        // Real wall time exceeds BOTH configured authentication windows. Neither
        // is weakened; the accepted resume has moved into authenticated life.
        long until = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < until)
        {
            server.TickSim(); await Task.Delay(10);
            Assert.False(session.Lost); Assert.False(server.Host.Transport.IsPeerClosed(resumedPeer));
            Assert.Equal(1, session.ResumeSuccesses);
        }
        Assert.Equal(shipId, session.World.MyShipId);
        Assert.Contains("Pilot1051", server.LivePlayerNames);
        session.SendLogoff(); await Pump(() => !server.LivePlayerNames.Contains("Pilot1051"));

        async Task Pump(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!condition() && !deadline.IsCancellationRequested) { server.TickSim(); await Task.Delay(5); }
            Assert.True(condition(), "P1051 real WS resume milestone timed out");
        }
    }
}
