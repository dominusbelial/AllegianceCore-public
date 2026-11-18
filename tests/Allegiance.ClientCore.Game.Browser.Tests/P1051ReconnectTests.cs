using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1051ReconnectTests
{
    [Fact]
    public async Task Successive_resumes_keep_the_initial_canonical_name_and_frozen_endpoint()
    {
        await using var session = new ConnectionSession
        { WebSocketEndpoint = WebSocketEndpoint.FromServerListing("ws://localhost:8048/game") };
        var peers = new List<Peer>();
        var names = new List<string>();
        var endpoints = new List<string>();
        Func<ITransportSite, ITransport> factory = site =>
        {
            endpoints.Add(((WebSocketEndpoint)typeof(ConnectionSession).GetField("_joinedWebSocketEndpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!).Uri.AbsoluteUri);
            var peer = new Peer(site, peers.Count == 0, names); peers.Add(peer); return peer;
        };
        typeof(ConnectionSession).GetProperty("TransportFactoryForTesting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, factory);
        session.EnableAutoReconnect();
        await session.ConnectAsync(6073, "TypedPilot");
        session.SendLogonRequest(new LogonRequest { CharacterName = "TypedPilot", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 });
        session.WebSocketEndpoint = WebSocketEndpoint.FromServerListing("ws://localhost:9999/changed");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            peers[^1].Drop();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (session.ResumeSuccesses < attempt || session.Reconnecting) await Task.Delay(5, timeout.Token);
            Assert.False(session.Lost);
        }
        Assert.Equal(new[] { "TypedPilot", "CanonicalPilot", "CanonicalPilot", "CanonicalPilot" }, names);
        Assert.All(endpoints, endpoint => Assert.Equal("ws://localhost:8048/game", endpoint));
    }

    private sealed class Peer(ITransportSite site, bool initial, List<string> names) : ITransport
    {
        public string ProtocolName => "ws";
        public bool IsConnected { get; private set; }
        public ConnectionId LocalId => ConnectionId.Server;
        public ushort BoundPort => 0;
        public void Host(ushort port = 0) => throw new NotSupportedException();
        public void Join(string address, ushort port, string name) => throw new NotSupportedException();
        public Task JoinAsync(string address, ushort port, string name, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); IsConnected = true; return Task.CompletedTask; }
        public void Shutdown() => IsConnected = false;
        public void Drop() { IsConnected = false; site.OnSessionLost(); }
        public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed)
        {
            foreach (byte[] message in FedPacket.Unpack(data))
            {
                if (FedMessageHeader.Read(message).Fmid != LogonRequest.Fmid) continue;
                var request = LogonRequest.Decode(message); names.Add(request.CharacterName!);
                site.OnReceive(ConnectionId.Server, FedPacket.Pack(new LogonAck
                {
                    FValidated = true, ShipID = 123,
                    CharNameOrFailureReason = initial ? "CanonicalPilot" : "Welcome back " + request.CharacterName,
                }.Encode()));
            }
        }
        public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int key) => Send(target, data, false);
        public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int key) => Send(target, data, true);
    }
}
