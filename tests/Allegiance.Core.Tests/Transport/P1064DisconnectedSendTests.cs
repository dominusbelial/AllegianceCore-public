using System.Reflection;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using FltBot = Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P1064DisconnectedSendTests
{
    private sealed class HostSite : ITransportSite
    {
        public ConnectionId Peer;
        public readonly ManualResetEventSlim Joined = new();
        public void OnPeerJoined(ConnectionId id, string name) { Peer = id; Joined.Set(); }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    // Capture only in the TEST: baseline's unhandled receive-thread exception
    // becomes an assertion with the original Bot.Send stack, rather than killing
    // the test host and hiding every later case.
    private sealed class ObservedSite(FltBot bot) : ITransportSite
    {
        public Exception? Error;
        public readonly ManualResetEventSlim Finished = new();
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            try { ((ITransportSite)bot).OnReceive(from, data); }
            catch (Exception ex) { Error = ex; }
            finally { Finished.Set(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inflight_bot_reply_after_sender_removed_does_not_throw(bool senderFailure)
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var bot = new FltBot();
        var site = new ObservedSite(bot);
        var hostSite = new HostSite();
        var host = new TcpTransport(hostSite);
        var client = new TcpTransport(site);
        typeof(FltBot).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, client);
        bot.InboundFrameDelayHook = fmid =>
        {
            if (fmid != Ping.Fmid) return;
            held.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("release held bot frame");
        };
        try
        {
            host.Host(0);
            client.Join("127.0.0.1", host.BoundPort, "disconnect-race");
            Assert.True(hostSite.Joined.Wait(TimeSpan.FromSeconds(5)));
            // Two assignments arm the real scripted warp handshake. The fresh
            // PING will send a pose reply from inside Bot.OnReceive.
            var poseBytes = new byte[ServerShipUpdate.Width];
            poseBytes[0] = 1;
            var reader = new WireReader(poseBytes);
            var pose = ServerShipUpdate.DecodeBody(ref reader);
            host.Send(hostSite.Peer, FedPacket.Pack(new[]
            {
                new SetCluster { SectorId = 0, Ship = pose }.Encode(),
                new SetCluster { SectorId = 1, Ship = pose }.Encode(),
                new Ping { TimeServer = 1000 }.Encode(),
            }), guaranteed: true);
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)), "receive callback entered before disconnect");
            if (senderFailure)
                // The sender worker calls this exact teardown path on a write
                // error/timeout; force its ordering while the reader is held.
                typeof(TcpTransport).GetMethod("DropClient", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, null);
            else
                client.Shutdown(); // clears sender, then bounded reader join
            Assert.Null(typeof(TcpTransport).GetField("_clientSender", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
            release.Set();
            Assert.True(site.Finished.Wait(TimeSpan.FromSeconds(5)), "callback completed");
            Assert.Null(site.Error);
            Assert.Single(bot.Pings); // callback really dispatched after teardown
            client.Send(ConnectionId.Server, FedPacket.Pack(new Ping().Encode()), true);
        }
        finally
        {
            release.Set();
            client.Shutdown();
            host.Shutdown();
            site.Finished.Dispose();
            hostSite.Joined.Dispose();
        }
    }

    [Fact]
    public void Send_without_a_successful_join_remains_a_programming_error()
    {
        var client = new TcpTransport(new HostSite());
        Assert.Throws<InvalidOperationException>(() => client.Send(ConnectionId.Server, new byte[4], true));
        client.Shutdown();
        Assert.Throws<InvalidOperationException>(() => client.Send(ConnectionId.Server, new byte[4], true));
        client.ConnectClientForTesting = (_, _, _, _) => throw new System.Net.Sockets.SocketException();
        Assert.Throws<System.Net.Sockets.SocketException>(() => client.Join("127.0.0.1", 1, "failed-join"));
        Assert.Throws<InvalidOperationException>(() => client.Send(ConnectionId.Server, new byte[4], true));
    }
}
