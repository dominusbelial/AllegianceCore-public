using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P646 (P645 follow-up) — an idle NON-BLOCKING pool-mode peer must not spin
/// on would-block. P645 made pool sockets non-blocking so the shared worker's
/// sends never block; as a result the read loop threw+c caught a would-block
/// IOException roughly every millisecond on an idle peer (~390k exceptions/s
/// at 390 idle peers — burning CPU and allocating exception objects). The fix
/// parks the reader in <c>Socket.Poll(SelectRead)</c> before Read, so no
/// exception is thrown when no data is pending. This test pins that: an idle
/// pool peer accrues ~0 would-block catches over 1 s, not ~1000.
/// </summary>
[Collection("LiveNetwork")]
public class TcpTransportNoWouldBlockSpinTests
{
    private sealed class Site : ITransportSite
    {
        public readonly List<ConnectionId> Joined = new();
        public void OnPeerJoined(ConnectionId id, string name) { lock (Joined) Joined.Add(id); }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    [Fact]
    public void Idle_pool_peer_accrues_almost_no_would_block_catches_over_one_second()
    {
        var site = new Site();
        var host = new TcpTransport(site, TransportMode.Pool, poolWorkers: 2);
        try
        {
            host.Host(0);

            var raw = new TcpClient();
            raw.NoDelay = true;
            raw.Connect("127.0.0.1", host.BoundPort);
            var ns = raw.GetStream();
            var name = Encoding.UTF8.GetBytes("IDLEWB");
            var greet = new byte[1 + 4 + name.Length];
            greet[0] = 0x01;
            BinaryPrimitives.WriteUInt32LittleEndian(greet.AsSpan(1), (uint)name.Length);
            name.CopyTo(greet.AsSpan(5));
            ns.Write(greet);
            ns.Flush();

            // Wait for the host to accept the peer and spin up its read loop.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                lock (site.Joined) { if (site.Joined.Count > 0) break; }
                if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined");
                Thread.Sleep(5);
            }
            Thread.Sleep(300);   // let the reader settle into Poll

            long c0 = TcpTransport.WouldBlockCatches;
            long w0 = TcpTransport.WouldBlockWaits;

            // Stay idle (no sends, no receives) for one second.
            Thread.Sleep(1000);

            long catches = TcpTransport.WouldBlockCatches - c0;
            long waits = TcpTransport.WouldBlockWaits - w0;

            // The efficient-block path must be engaged (it parked in Poll).
            Assert.True(waits > 0, "reader never parked in Poll — efficient blocking not engaged");
            // And it must NOT be throwing+catching would-block every millisecond.
            Assert.True(catches < 20,
                $"idle pool peer accrued {catches} would-block catches in 1 s — the ~1 ms exception spin is back");

            raw.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }
}
