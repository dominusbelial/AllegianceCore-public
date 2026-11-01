using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// The session keep-alive thread runs before Connect and sends as soon as the
/// transport reports connected. Join must therefore not expose the client as
/// connected until the greeting is queued, or the server drops the connection
/// for a non-greet first frame.
/// </summary>
[Collection("LiveNetwork")]
public sealed class TcpClientGreetOrderTests
{
    private sealed class Site : ITransportSite
    {
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    [Fact]
    public void Send_racing_join_never_precedes_the_greeting_or_finds_no_sender()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        int nonGreet = 0, notConnected = 0;
        byte[] packet = new byte[8];
        for (int i = 0; i < 2000; i++)
        {
            var client = new TcpTransport(new Site());
            bool stop = false;
            var racer = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    if (!client.IsConnected) continue;
                    try { client.Send(ConnectionId.Server, packet, guaranteed: true); }
                    catch (InvalidOperationException) { Interlocked.Increment(ref notConnected); }
                    return;
                }
            }) { IsBackground = true };
            racer.Start();
            client.Join("127.0.0.1", port, "pilot");
            using var accepted = listener.AcceptTcpClient();
            accepted.ReceiveTimeout = 5000;
            if (accepted.GetStream().ReadByte() != 1) nonGreet++;
            Volatile.Write(ref stop, true);
            racer.Join();
            client.Shutdown();
        }
        listener.Stop();
        Assert.Equal(0, nonGreet);
        Assert.Equal(0, notConnected);
    }
}
