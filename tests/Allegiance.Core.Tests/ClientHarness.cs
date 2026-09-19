using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    private readonly List<byte> _streamBuf = new();   // byte-accurate accumulate of every received chunk

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

    /// <summary>Discard all packets received so far (buffered in <see cref="_streamBuf"/>).</summary>
    public void DrainAll()
    {
        lock (_gate) _streamBuf.Clear();
    }

    /// <summary>
    /// Block until a message with <paramref name="fmid"/> arrives (scanning the
    /// byte-accurate stream buffer, consuming whole packets), then return its raw
    /// bytes. Throws <see cref="TimeoutException"/> if <paramref name="timeoutMs"/>
    /// elapses first. Robust to TCP coalescing/splitting (accumulates chunks and
    /// consumes complete packets) — unlike <see cref="WaitReceive"/>'s
    /// chunk-is-one-packet assumption.
    /// </summary>
    public byte[] WaitForFmid(ushort fmid, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            byte[]? found;
            lock (_gate)
            {
                found = ConsumePacketsScan(fmid);
                if (found != null) return found;
            }
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"timed out waiting for server fmid {fmid}");
            Thread.Sleep(5);
        }
    }

    /// <summary>Called under <see cref="_gate"/>: consume complete packets from
    /// the front of <see cref="_streamBuf"/>, returning the first message with
    /// the given <paramref name="want"/> fmid if any, else null. Robust to TCP
    /// coalescing/splitting: it walks the stream message-by-message and peels a
    /// packet exactly when the accumulated messages' CRC matches the trailing 4
    /// bytes, so several server packets concatenated in one chunk are all
    /// consumed (and the desired message found) rather than treated as one
    /// oversized non-matching packet.</summary>
    private byte[]? ConsumePacketsScan(ushort want)
    {
        byte[]? result = null;
        while (true)
        {
            if (_streamBuf.Count < 4 + 2) break;   // need ≥ one message header + CRC slot
            // Gather messages from buffer offset 0, checking after each whether
            // they form a complete, CRC-valid packet.
            int msgEnd = 0;
            bool peeled = false;
            while (true)
            {
                if (msgEnd + 4 <= _streamBuf.Count && msgEnd > 0)
                {
                    uint expected = BinaryPrimitives.ReadUInt32LittleEndian(CollectionsMarshal.AsSpan(_streamBuf).Slice(msgEnd));
                    uint actual = FedMsgCrc.Compute(CollectionsMarshal.AsSpan(_streamBuf).Slice(0, msgEnd));
                    if (actual == expected)
                    {
                        // Valid packet [0, msgEnd+4) — walk its messages.
                        int m = 0;
                        while (m < msgEnd)
                        {
                            ushort cb = BinaryPrimitives.ReadUInt16LittleEndian(CollectionsMarshal.AsSpan(_streamBuf).Slice(m));
                            var msg = new byte[cb];
                            for (int k = 0; k < cb; k++) msg[k] = _streamBuf[m + k];
                            if (result is null && FedMessageHeader.Read(msg).Fmid == want) result = msg;
                            m += cb;
                        }
                        _streamBuf.RemoveRange(0, msgEnd + 4);
                        peeled = true;
                        break;
                    }
                }
                // Not a packet boundary here — extend by the next message, if it is whole.
                if (msgEnd + 2 > _streamBuf.Count) break;                 // partial message header — wait
                ushort nextCb = BinaryPrimitives.ReadUInt16LittleEndian(CollectionsMarshal.AsSpan(_streamBuf).Slice(msgEnd));
                if (nextCb < 4 || msgEnd + nextCb > _streamBuf.Count) break;  // partial message body — wait
                msgEnd += nextCb;
            }
            if (!peeled) break;              // nothing whole available — wait for more bytes
            if (result != null) return result;   // found it; consume and report
            // else: peeled a packet with no match — keep scanning the next one.
        }
        return result;
    }

    public void Send(byte[] packet) =>
        _transport!.Send(ConnectionId.Server, packet, guaranteed: true);

    /// <summary>P281 — request a side in the post-connect side-select flow
    /// (FM_C_POSITIONREQ). The server seats the peer on <paramref name="sideId"/>
    /// and sends the deferred join/launch burst (incl. BUY_LOADOUT_ACK).</summary>
    public void RequestSide(short sideId) =>
        Send(FedPacket.Pack(new[] { new PositionReq { SideId = sideId }.Encode() }));

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
        lock (_gate)
        {
            _received.Add(data.ToArray());
            foreach (var b in data) _streamBuf.Add(b);
        }
    }
}
