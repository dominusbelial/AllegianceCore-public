using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// Loopback proof of the <see cref="TcpTransport"/> over a **real** TCP socket
/// (not a mock, not an in-memory pipe), exercising the existing <c>Wire</c>
/// codec end-to-end in both directions. This is the "cheapest possible
/// falsification" from TRANSPORT_SEAM.md §Recommendation: if a LOGONREQ/
/// LOGONACK cannot round-trip over the socket with the shipped codec, the plan
/// is dead. Runs the client and host in the SAME process (the two-process
/// boundary is covered by <c>tools/loopback</c>).
/// </summary>
public class TcpLoopbackProofTests
{
    private static LogonRequest SampleRequest() => new()
    {
        CharacterName = "Komrad",
        ZoneTicket = new byte[] { 0x01, 0x02, 0x03, 0xAA, 0xBB },
        CDKey = "DOMINIUS",
        MissionPassword = "secret",
        FedsrvVer = LogonRequest.MsgVer, // 216
        Time = 987654321u,
        DwCookie = 0xCAFE_BABEu,
        CrcFileList = -12345,
        SteamAuthTicket = TicketWithMarkers(),
        SteamAuthTicketLength = 999,
        SteamID = 76561198000000000ul,
        DrmHash = "drmhash123",
    };

    private static byte[] TicketWithMarkers()
    {
        var t = new byte[1024];
        t[0] = 0xAB; t[1023] = 0xCD;
        return t;
    }
    // (Markers keep the byte-array from collapsing to empty after trailing-NUL
    // stripping, so the ticket comparison is meaningful in every round-trip.)

    private static LogonAck SampleAck() => new()
    {
        CharNameOrFailureReason = "Welcome Komrad",
        TimeServer = 888,
        ShipID = 42,
        FValidated = true,
        FRetry = false,
        Cookie = 0x77,
    };

    /// <summary>Waits up to <paramref name="ms"/> for <paramref name="cond"/>.</summary>
    private static void Eventually(Func<bool> cond, int ms = 5000, string what = "")
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms)
                throw new TimeoutException($"timed out waiting for {what}");
            Thread.Sleep(10);
        }
    }

    /// <summary>Collects site events with a monotonic sequence stamp so ordering
    /// constraints can be asserted, mirroring the serialized m_msgList queue the
    /// real message layer would funnel these through.</summary>
    private sealed class RecordingSite : ITransportSite
    {
        public sealed record Evt(long Seq, string Kind, ConnectionId Id, byte[]? Data = null, string? Name = null);
        public long Counter;
        public readonly List<Evt> Events = new();

        private long NextSeq() => Interlocked.Increment(ref Counter);
        private void Push(Evt e) { lock (Events) { Events.Add(e); } }

        public void OnPeerJoined(ConnectionId id, string name) => Push(new(NextSeq(), "JOINED", id, Name: name));
        public void OnPeerLeft(ConnectionId id) => Push(new(NextSeq(), "LEFT", id));
        public void OnSessionLost() => Push(new(NextSeq(), "LOST", default));
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Push(new(NextSeq(), "RECV", from, data.ToArray()));

        public Evt WaitJoined(int ms = 5000) => Poll(e => e.Kind == "JOINED", ms, "OnPeerJoined");
        public Evt WaitReceived(int ms = 5000) => Poll(e => e.Kind == "RECV", ms, "OnReceive");
        public Evt Joined() => Poll(e => e.Kind == "JOINED", 0, "no-JOINED");
        public int ReceiveCount { get { lock (Events) return Events.Count(e => e.Kind == "RECV"); } }

        public Evt WaitOccurrence(int n, int ms = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (Events)
                {
                    var recvs = Events.Where(e => e.Kind == "RECV").ToList();
                    if (recvs.Count >= n) return recvs[n - 1];
                }
                if (sw.ElapsedMilliseconds > ms) throw new TimeoutException($"timed out waiting for RECV#{n}");
                Thread.Sleep(5);
            }
        }

        private Evt Poll(Func<Evt, bool> pred, int ms, string what)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (Events)
                {
                    if (Events.FirstOrDefault(pred) is { } hit)
                        return hit;
                }
                if (sw.ElapsedMilliseconds > ms) throw new TimeoutException($"timed out waiting for {what}");
                Thread.Sleep(5);
            }
        }
    }

    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Logon_Handshake_RoundTrips_BothDirections_OverRealSocket(bool udp)
    {
        var siteHost = new RecordingSite();
        var siteClient = new RecordingSite();
        ITransport host = udp ? new UdpTransport(siteHost) : new TcpTransport(siteHost);
        ITransport client = udp ? new UdpTransport(siteClient) : new TcpTransport(siteClient);

        try
        {
            // 1. host binds an ephemeral port; client connects
            host.Host(0);
            Assert.True(host.BoundPort > 0, "host bound an ephemeral port");
            client.Join("127.0.0.1", host.BoundPort, "BOT-ONE");
            Eventually(() => client.IsConnected && siteHost.Events.Count(e => e.Kind == "JOINED") == 1,
                what: "connect");

            // OnPeerJoined must fire before OnReceive for that peer; the name travelled in-band
            var peerJoined = siteHost.Joined();
            Assert.Equal("BOT-ONE", peerJoined.Name);
            Assert.Equal((uint)1, peerJoined.Id.Value); // host minted the peer's id (self is 0)

            // 2. client sends FM_C_LOGONREQ — encoded by the EXISTING Wire codec,
            //    packed (messages + trailing CRC) by FedPacket, one frame per write.
            var request = SampleRequest();
            client.Send(ConnectionId.Server, FedPacket.Pack(new[] { request.Encode() }), guaranteed: true);

            // 3. host receives, unpacks + validates CRC, decodes to identical values (incl. var items)
            var recv = siteHost.WaitReceived();
            var messages = FedPacket.Unpack(recv.Data!);          // asserts CRC over message bytes
            var decodedReq = LogonRequest.Decode(messages.Single());
            AssertRoundTripped(request, decodedReq);
            Assert.Equal(recv.Id, peerJoined.Id);

            // 4. host replies FM_S_LOGONACK; client decodes — proving the reverse direction
            var ack = SampleAck();
            host.Send(peerJoined.Id, FedPacket.Pack(new[] { ack.Encode() }), guaranteed: true);
            var clientRecv = siteClient.WaitReceived();
            var ackMessages = FedPacket.Unpack(clientRecv.Data!);  // CRC validated on client side too
            var decodedAck = LogonAck.Decode(ackMessages.Single());
            AssertEqualAck(ack, decodedAck);

            // 6. ordering: JOINED stamped before RECV on the host
            Assert.True(peerJoined.Seq < recv.Seq,
                $"OnPeerJoined ({peerJoined.Seq}) must precede OnReceive ({recv.Seq})");

            // 5. a corrupted byte is rejected by the CRC check (2nd host receive).
            //    Flip a byte INSIDE the already-packed packet so the stored CRC
            //    no longer matches — recomputing the CRC after the flip would make
            //    it self-consistent and prove nothing.
            var packet = FedPacket.Pack(new[] { request.Encode() });
            packet[packet.Length / 2] ^= 0xFF;
            client.Send(ConnectionId.Server, packet, true);
            var corrupted = siteHost.WaitOccurrence(2);
            Assert.Throws<FedCrcException>(() => FedPacket.Unpack(corrupted.Data!));
        }
        finally
        {
            client.Shutdown();
            host.Shutdown();
        }
    }

    [Fact]
    public void PartialRead_FrameSplitAcrossTwoWrites_WithDelay_IsReassembled()
    {
        // The crux of framing: TCP splits messages in the field. We must not
        // merely assert the happy path — here we drive the FRAME byte-by-byte in
        // two chunks separated by a delay and prove the host reassembles it.
        var siteHost = new RecordingSite();
        var host = new TcpTransport(siteHost);
        try
        {
            host.Host(0);

            using var raw = new TcpClient();
            raw.Connect("127.0.0.1", host.BoundPort);
            var ns = raw.GetStream();

            // the transport's Greet frame (host learns the name, raises OnPeerJoined)
            var name = "RAW-CLIENT";
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var greet = Frame(0x01, nameBytes);
            ns.Write(greet); ns.Flush();
            Eventually(() => siteHost.ReceiveCount == 0 && siteHost.Events.Count(e => e.Kind == "JOINED") == 1,
                what: "OnPeerJoined for raw client");

            // a LOGONREQ packet, sent in TWO chunks with a delay between them
            var request = SampleRequest();
            var packet = FedPacket.Pack(new[] { request.Encode() });
            var frame = Frame(0x02, packet);
            int split = frame.Length / 2;
            ns.Write(frame.AsSpan(0, split)); ns.Flush();
            Thread.Sleep(150);                                    // force a stalled mid-frame read
            ns.Write(frame.AsSpan(split)); ns.Flush();

            var recv = siteHost.WaitReceived();
            var msgs = FedPacket.Unpack(recv.Data!);              // CRC proves the reassembled bytes are exact
            var decoded = LogonRequest.Decode(msgs.Single());
            AssertRoundTripped(request, decoded);
            Assert.Equal((uint)1, recv.Id.Value);
        }
        finally
        {
            host.Shutdown();
        }
    }

    private static byte[] Frame(byte type, byte[] body)
    {
        var f = new byte[1 + 4 + body.Length];
        f[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(1), (uint)body.Length);
        body.CopyTo(f.AsSpan(5));
        return f;
    }

    // ── P320 — client-half session-loss detection ─────────────────────────

    /// <summary>P320 — when the server (host) dies, the client half must surface
    /// <see cref="ITransportSite.OnSessionLost"/> (exactly once) and clear
    /// <see cref="TcpTransport.IsConnected"/> instead of staying silently
    /// "connected" forever — the pre-fix behaviour that left the client rendering
    /// a frozen world with no indication (review H-4).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientHalf_RaisesOnSessionLost_AndClearsConnected_WhenServerDies(bool udp)
    {
        var siteHost = new RecordingSite();
        var siteClient = new RecordingSite();
        ITransport host = udp ? new UdpTransport(siteHost) : new TcpTransport(siteHost);
        ITransport client = udp ? new UdpTransport(siteClient) : new TcpTransport(siteClient);

        try
        {
            host.Host(0);
            client.Join("127.0.0.1", host.BoundPort, "BOT-ONE");
            Eventually(() => client.IsConnected && siteHost.Events.Count(e => e.Kind == "JOINED") == 1,
                what: "client connected");

            // Simulate a server crash: tear the host down. Closing the host's
            // transport closes the peer socket, so the client's read loop unwinds
            // and must detect the loss.
            host.Shutdown();

            Eventually(() => siteClient.Events.Count(e => e.Kind == "LOST") == 1,
                what: "OnSessionLost on the client half");
            Assert.False(client.IsConnected, "IsConnected must clear once the server dies");
            Assert.Equal(1, siteClient.Events.Count(e => e.Kind == "LOST"));
        }
        finally
        {
            client.Shutdown();
            host.Shutdown();
        }
    }

    /// <summary>P320 — a deliberate client <see cref="TcpTransport.Shutdown"/>
    /// (e.g. the user quits) must NOT be reported as a session loss: the close is
    /// intentional, not an unexpected server drop.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntentionalClientShutdown_DoesNotRaiseOnSessionLost(bool udp)
    {
        var siteHost = new RecordingSite();
        var siteClient = new RecordingSite();
        ITransport host = udp ? new UdpTransport(siteHost) : new TcpTransport(siteHost);
        ITransport client = udp ? new UdpTransport(siteClient) : new TcpTransport(siteClient);

        try
        {
            host.Host(0);
            client.Join("127.0.0.1", host.BoundPort, "BOT-ONE");
            Eventually(() => client.IsConnected && siteHost.Events.Count(e => e.Kind == "JOINED") == 1,
                what: "client connected");

            // Deliberate quit — closes the socket, but this is NOT a loss.
            client.Shutdown();
            Thread.Sleep(200);   // give the read loop time to unwind
            Assert.DoesNotContain(siteClient.Events, e => e.Kind == "LOST");
            Assert.False(client.IsConnected);
        }
        finally
        {
            client.Shutdown();
            host.Shutdown();
        }
    }

    private static void AssertRoundTripped(LogonRequest exp, LogonRequest got)
    {
        Assert.Equal(exp.CharacterName, got.CharacterName);
        Assert.Equal(exp.ZoneTicket, got.ZoneTicket);
        Assert.Equal(exp.CDKey, got.CDKey);
        Assert.Equal(exp.MissionPassword, got.MissionPassword);
        Assert.Equal(exp.FedsrvVer, got.FedsrvVer);
        Assert.Equal(exp.Time, got.Time);
        Assert.Equal(exp.DwCookie, got.DwCookie);
        Assert.Equal(exp.CrcFileList, got.CrcFileList);
        Assert.Equal(exp.SteamAuthTicket, got.SteamAuthTicket);
        Assert.Equal(exp.SteamAuthTicketLength, got.SteamAuthTicketLength);
        Assert.Equal(exp.SteamID, got.SteamID);
        Assert.Equal(exp.DrmHash, got.DrmHash);
    }

    private static void AssertEqualAck(LogonAck exp, LogonAck got)
    {
        Assert.Equal(exp.CharNameOrFailureReason, got.CharNameOrFailureReason);
        Assert.Equal(exp.TimeServer, got.TimeServer);
        Assert.Equal(exp.ShipID, got.ShipID);
        Assert.Equal(exp.FValidated, got.FValidated);
        Assert.Equal(exp.FRetry, got.FRetry);
        Assert.Equal(exp.Cookie, got.Cookie);
    }
}
