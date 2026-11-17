using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests.Transport;

/// <summary>Runs existing authority regressions through a real WS peer and the
/// production ServerHost event drain, without advancing the fixture's simulation clock.</summary>
internal sealed class WebSocketRegressionIngress : IDisposable
{
    private readonly ServerHost _host;
    private readonly Action<ITransportEvent> _dispatch;
    private readonly WebSocketServerTransport _transport;
    private readonly List<ClientWebSocket> _clients = [];
    internal WebSocketRegressionIngress(Srv server, ServerHost host, int peers)
    {
        _host = host;
        _dispatch = typeof(Srv).GetMethod("HandleTransportEvent", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action<ITransportEvent>>(server);
        host.Transport?.Shutdown();
        _transport = new WebSocketServerTransport(host, P1048WebSocketTests.Settings());
        host.Transport = _transport;
        try
        {
            _transport.Host(0, IPAddress.Loopback);
            for (int i = 0; i < peers; i++) _clients.Add(P1048WebSocketTests.Connect(_transport.BoundPort).GetAwaiter().GetResult());
            // A receive is serialized behind its join; Send waits for publication
            // and Drain below applies the entire selected FIFO, including that join.
        }
        catch { Dispose(); throw; }
    }
    internal WebSocketRegressionIngress(Srv server, int peers) : this(server,
        (ServerHost)typeof(Srv).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!, peers) { }
    internal void Send(ConnectionId peer, byte[] packet)
    {
        long before = _host.PacketsReceived;
        P1048WebSocketTests.Send(_clients[checked((int)peer.Value - 1)], packet).GetAwaiter().GetResult();
        P1048WebSocketTests.Until(() => _host.PacketsReceived > before).GetAwaiter().GetResult();
        _host.Drain(_dispatch);
    }
    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        _transport.Shutdown();
    }
}
