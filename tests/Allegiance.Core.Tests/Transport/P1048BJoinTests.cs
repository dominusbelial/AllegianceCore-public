using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.Replay;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Identity;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Server;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
public sealed class P1048BJoinTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Signed_ticket_side_ready_launch_input_and_updates_use_the_same_ordered_protocol_over_TCP_and_WS()
    {
        Require.Artwork();
        var tcp = await Scenario(false); var ws = await Scenario(true);
        output.WriteLine(JsonSerializer.Serialize(new { tcp, ws }));
        // Compare the transaction milestones, preserving every occurrence and order.
        // Tick-driven broadcasts are separately retained in the full sequences below.
        ushort[] milestones = [LogonAck.Fmid, JoinedMission.Fmid, PlayerReady.Fmid, BuyLoadoutAck.Fmid, SetCluster.Fmid];
        Assert.Equal(tcp.Types.Where(milestones.Contains), ws.Types.Where(milestones.Contains));
        Assert.True(tcp.Moved && ws.Moved && tcp.InputAccepted && ws.InputAccepted);
        string evidence = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1048b", "desktop-join.json");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new { tcp, ws }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed record Result(string Protocol, ushort[] Types, bool Moved, string CanonicalName, int ShipUpdates, bool InputAccepted);
    private async Task<Result> Scenario(bool webSocket)
    {
        string scratch = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1048b", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Path.Combine(scratch, "public.pem"), recording = Path.Combine(scratch, "wire.gz");
        await File.WriteAllTextAsync(publicKey, key.ExportSubjectPublicKeyInfoPem());
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, BindAddress = "127.0.0.1", UpdatesPerSecond = 20,
                Discovery = new() { Enabled = false }, WebSocket = P1048WebSocketTests.Settings(),
                Identity = new() { Issuer = "p1048b", Audience = "game", PublicKeyFile = publicKey, AllowAnonymous = false },
                Games = [new() { Name = "P1048B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
                    AutoStart = true, StartCountdownSeconds = 0, AllowEmptyTeams = true }],
            });
            using var wire = new WireRecording(recording);
            using var session = new ConnectionSession { Recording = wire, TransportProtocol = "tcp",
                WebSocketEndpoint = webSocket ? WebSocketEndpoint.FromConfiguration($"ws://127.0.0.1:{server.WebSocketPort}/game") : null,
                WebSocketOrigin = webSocket ? "http://localhost" : null };
            var request = new LogonRequest { CharacterName = "Spoofed", FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue, CrcFileList = -1, Time = 1 };
            var join = session.JoinAsync(server.BoundPort, "P1048BPilot", request, identityTicket: _ => Task.FromResult(
                JoinTokens.Issue(key, "p1048b", "game", "0123456789abcdef0123456789abcdef", "P1048BPilot", DateTimeOffset.UtcNow)));
            await Pump(() => join.IsCompleted); Assert.Null(await join);
            Assert.True(session.Ack!.FValidated); Assert.Equal("P1048BPilot", session.Ack.CharNameOrFailureReason);
            Assert.Equal(webSocket ? "ws" : "tcp", session.ActiveTransportProtocol);
            session.SendPositionReq(0);
            await Pump(() => session.World.MyShipId is not null && server.Ships.Count == 1);
            short shipId = session.World.MyShipId!.Value;
            Assert.Equal((short)0, server.Ships.Single().Ship.SideId);
            session.SendPlayerReady(true, shipId);
            await Pump(() => server.Missions[0].Stage == Allegiance.Core.Sim.MissionStage.Started);
            var ship = server.Ships.Single().Ship;
            long sectorGeneration = session.World.SectorGeneration;
            session.ResetBuyLoadoutAcks();
            session.SendBuyLoadout(ship.HullType.HullId, true);
            await Pump(() => ship.StationId is null && session.BuyLoadoutAcks.Any(ack => ack.FLaunch)
                && session.World.SectorGeneration > sectorGeneration);
            await Pump(() => session.World.Ships.TryGetValue(shipId, out var own) && own.SectorId >= 0);
            var before = ship.Position;
            long decoded = session.DecodedBytes;
            session.CommandControls(new ControlData(.125f, -.25f, 0, 1), 1);
            await Pump(() => Math.Abs(ship.Controls.Yaw - .125f) < .02f && Math.Abs(ship.Controls.Pitch + .25f) < .02f
                && ship.Position != before && session.DecodedBytes > decoded);
            Assert.Contains("P1048BPilot", server.LivePlayerNames);
            await session.ShutdownAsync(); wire.Dispose();
            var messages = new BattleReplay(recording).Packets.SelectMany(p => FedPacket.Unpack(p.Payload)).ToArray();
            ushort[] types = messages.Select(m => FedMessageHeader.Read(m).Fmid).ToArray();
            Assert.Contains(SingleShipUpdate.Fmid, types);
            return new(webSocket ? "ws" : "tcp", types, ship.Position != before, "P1048BPilot", types.Count(t => t == SingleShipUpdate.Fmid), true);

            async Task Pump(Func<bool> condition)
            {
                using var deadline = new CancellationTokenSource(8000);
                while (!condition() && !deadline.IsCancellationRequested) { server.TickSim(); await Task.Delay(2); }
                Assert.True(condition(), $"{(webSocket ? "ws" : "tcp")} join milestone timed out; ack={session.Ack?.FValidated}, lost={session.Lost}");
            }
        }
        finally { Directory.Delete(scratch, true); }
    }
}
