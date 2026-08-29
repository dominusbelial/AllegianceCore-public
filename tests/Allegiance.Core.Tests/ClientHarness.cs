using System.Diagnostics;
using System.Net.Sockets;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// Thin wrapper over <see cref="TcpTransport"/> for the server end-to-end
/// tests. Acts as the client half: joins the server, sends a LOGONREQ and waits
/// for the LOGONACK, and can drop the socket abruptly. Implements
/// <see cref="ITransportSite"/> just to capture receives on the client side
/// (the client half gets no lifecycle callbacks — see TcpTransport.Join).
/// </summary>
internal sealed class ClientHarness : ITransportSite, IDisposable
{
    private TcpTransport? _transport;
    private readonly object _gate = new();
    private readonly List<byte[]> _received = new();

    public bool Connected => _transport?.IsConnected ?? false;

    public void Join(ushort port, string name)
    {
        _transport = new TcpTransport(this);
        _transport.Join("127.0.0.1", port, name);
    }

    /// <summary>Send a logon request and block until the ACK arrives; return the decoded ACK.</summary>
    public LogonAck RequestLogon(LogonRequest req)
    {
        Send(Pack(req));
        byte[] data = WaitReceive(5000);
        var msgs = FedPacket.Unpack(data);
        return LogonAck.Decode(msgs.Single());
    }

    public void Send(byte[] packet) =>
        _transport!.Send(ConnectionId.Server, packet, guaranteed: true);

    /// <summary>Tear the client socket down (the server sees EOF / a dropped peer).</summary>
    public void AbortTransport() => _transport?.Shutdown();

    /// <summary>Idempotent close of the harness transport.</summary>
    public void Shutdown() => _transport?.Shutdown();

    /// <summary>Dispose = Shutdown, for use in a <c>using</c>.</summary>
    public void Dispose() => Shutdown();

    private byte[] WaitReceive(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if (_received.Count > 0) return _received[0];
            }
            if (sw.ElapsedMilliseconds > ms)
                throw new TimeoutException("timed out waiting for server ACK");
            Thread.Sleep(5);
        }
    }

    private byte[] Pack(LogonRequest req) => FedPacket.Pack(new[] { req.Encode() });

    // ── ITransportSite (client half: only OnReceive is raised) ────────────
    void ITransportSite.OnPeerJoined(ConnectionId id, string name) { }
    void ITransportSite.OnPeerLeft(ConnectionId id) { }
    void ITransportSite.OnSessionLost() { }
    void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        lock (_gate) _received.Add(data.ToArray());
    }
}
