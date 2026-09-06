using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Session;

/// <summary>
/// A <see cref="TcpTransport"/> client session that drives a <see cref="ClientWorld"/>.
/// Closely modeled on AllegianceCore's own <c>Bot.cs</c>: it connects, sends a
/// LOGONREQ, waits for the ACK, commands controls as a real <c>FM_C_SHIP_UPDATE(24)</c>,
/// and on the receive path decodes inbound messages — feeding exactly the three
/// message types <see cref="ClientWorld"/> understands (plus <see cref="LogonAck"/>
/// for connect proof) into <see cref="World"/> under a lock.
///
/// <see cref="World"/> is exposed the same way <c>Bot.cs</c> exposes its
/// <c>IReadOnlyList&lt;T&gt;</c> properties: it is the single live model, its
/// mutations all happen under <see cref="_gate"/> on the receive thread, and a
/// caller reads it (after a bounded wait/pump) on its own thread without holding
/// the lock. This is the client half of the transport, so only
/// <see cref="ITransportSite.OnReceive"/> is raised.
/// </summary>
public sealed class ConnectionSession : ITransportSite, IDisposable
{
    private TcpTransport? _transport;
    private readonly object _gate = new();
    private readonly ClientWorld _world = new();
    private LogonAck? _ack;

    /// <summary>True while the underlying transport is connected.</summary>
    public bool Connected => _transport?.IsConnected ?? false;

    /// <summary>The last LOGONACK (or rejection) received; null until one arrives.</summary>
    public LogonAck? Ack { get { lock (_gate) return _ack; } }

    /// <summary>The live world model, upserted on the receive thread under
    /// <see cref="_gate"/>. Mutations happen on the transport thread; a caller
    /// should wait for the state it expects (e.g. <see cref="ClientWorld.MyShipId"/>)
    /// rather than read blind.</summary>
    public ClientWorld World => _world;

    /// <summary>Connect to a hosting server on loopback and register our name.</summary>
    public void Connect(ushort port, string playerName)
    {
        _transport = new TcpTransport(this);
        _transport.Join("127.0.0.1", port, playerName);
    }

    /// <summary>Send a LOGONREQ; the ACK (or rejection) is received asynchronously.</summary>
    public void SendLogonRequest(LogonRequest req) =>
        Send(FedPacket.Pack(new[] { req.Encode() }));

    /// <summary>Block until a LOGONACK arrives (or <paramref name="timeoutMs"/>). Returns the ACK.</summary>
    public LogonAck WaitForAck(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var ack = Ack;
            if (ack is not null) return ack;
            Thread.Sleep(5);
        }
        throw new TimeoutException("timed out waiting for LOGONACK");
    }

    /// <summary>Command the ship's controls: the four axes and the movement-bit
    /// <paramref name="stateM"/>, encoded as a real <c>FM_C_SHIP_UPDATE(24)</c>.
    /// Mirror of <c>Bot.CommandControls</c>.</summary>
    public void CommandControls(ControlData controls, int stateM = 0)
    {
        var msg = new ShipUpdate
        {
            Time = 0u,              // client's own time — the server reads controls only
            Cookie = (byte)(Ack?.Cookie ?? 0),
            Payload = ClientShipUpdate.FromControls(0u, stateM, controls),
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    private void Send(byte[] packet) =>
        _transport!.Send(ConnectionId.Server, packet, guaranteed: true);

    // ── ITransportSite (client half: only OnReceive is raised) ─────────────
    void ITransportSite.OnPeerJoined(ConnectionId id, string name) { }
    void ITransportSite.OnPeerLeft(ConnectionId id) { }
    void ITransportSite.OnSessionLost() { }

    void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        byte[][] messages;
        try { messages = FedPacket.Unpack(data.ToArray()); }
        catch (FedCrcException) { return; }   // a bad frame is not ours to adjudicate

        lock (_gate)
        {
            foreach (var m in messages)
            {
                ushort fmid;
                try { fmid = FedMessageHeader.Read(m).Fmid; }
                catch { continue; }

                switch (fmid)
                {
                    case LogonAck.Fmid:
                        _ack = LogonAck.Decode(m);
                        break;
                    case SetCluster.Fmid:
                        _world.Apply(SetCluster.Decode(m));
                        break;
                    case SingleShipUpdate.Fmid:
                        _world.Apply(SingleShipUpdate.Decode(m));
                        break;
                    case ShipStatusMessage.Fmid:
                        _world.Apply(ShipStatusMessage.Decode(m));
                        break;
                }
            }
        }
    }

    /// <summary>Close the client socket. The server sees EOF / a dropped peer.</summary>
    public void Shutdown() => _transport?.Shutdown();

    public void Dispose() => Shutdown();
}
