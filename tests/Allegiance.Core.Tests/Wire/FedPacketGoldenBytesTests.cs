using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// P619 — golden-bytes proof that the allocation-light <see cref="FedPacket"/>
/// pack overloads added for the per-tick broadcast allocation cut produce
/// EXACTLY the same packet bytes as the original <see cref="IEnumerable{T}"/>
/// pack path. Wire bytes must stay identical when the server re-routes its hot
/// broadcast sends through <c>Pack(byte[])</c> (single frame) and
/// <c>Pack(ReadOnlySpan&lt;byte[]&gt;)</c> (frame batch) — this file pins that
/// identity across a range of payload shapes, including the trailing CRC (each
/// packed packet must still round-trip through <see cref="FedPacket.Unpack"/>
/// with a valid checksum).
/// </summary>
public class FedPacketGoldenBytesTests
{
    private static byte[] Msg(params byte[] payload)
    {
        // A plausible message frame the packer treats as an opaque blob (the
        // pack path concatenates bytes + appends a CRC; it does not parse them).
        var b = new byte[payload.Length];
        Array.Copy(payload, b, payload.Length);
        return b;
    }

    private static byte[][] SampleMessages()
    {
        // Various shapes: empty, tiny, a realistic ~30-60B ship update, and a
        // larger multi-frame payload — covering LOH-irrelevant sizes and the
        // 1-message / many-message extremes.
        return
        [
            Array.Empty<byte>(),
            Msg(0x01),
            Msg(0x4E, 0x00, 0x00, 0x00, 0x78, 0x00, 0x05, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3F),
            Msg(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()),
            Msg(Enumerable.Range(0, 333).Select(i => (byte)(i % 251)).ToArray()),
        ];
    }

    [Fact]
    public void Single_message_pack_matches_original_array_path_byte_for_byte()
    {
        foreach (var m in SampleMessages())
        {
            byte[] fast = FedPacket.Pack(m);                       // new single-frame overload
            byte[] reference = FedPacket.Pack(new[] { m });        // original IEnumerable path
            Assert.Equal(reference, fast);                         // element-wise byte equality
            Assert.Equal(m.Length + FedPacket.CrcWidth, fast.Length);
        }
    }

    [Fact]
    public void Span_list_pack_matches_original_enumerable_path_byte_for_byte()
    {
        var samples = SampleMessages();
        // Exercises: empty, single, and many-message batches, incl. a batch that
        // mimics a per-recipient teammate posBatch.
        int[][] plans =
        [
            [],
            [0],
            [0, 1],
            [0, 1, 2, 3, 4],
            [4, 0, 2, 4, 1, 3, 4, 4],
        ];
        foreach (var plan in plans)
        {
            byte[][] batch = plan.Select(i => samples[i]).ToArray();
            byte[] fast = FedPacket.Pack((ReadOnlySpan<byte[]>)batch);   // new span overload
            byte[] reference = FedPacket.Pack((IEnumerable<byte[]>)batch); // original path
            Assert.Equal(reference, fast);
            int expectedLen = batch.Sum(b => b.Length) + FedPacket.CrcWidth;
            Assert.Equal(expectedLen, fast.Length);
        }
    }

    [Fact]
    public void Fast_packed_packets_round_trip_through_unpack_with_valid_crc()
    {
        // Build self-consistent frames (cbmsg in the first 2 LE bytes == length)
        // so Unpack's frame walk can decode them back; the identity tests above
        // already pin byte-equality for arbitrary (even malformed) payloads.
        static byte[] ValidFrame(int len)
        {
            var b = new byte[len];
            b[0] = (byte)len;
            b[1] = (byte)(len >> 8);
            for (int i = 4; i < len; i++) b[i] = (byte)(i % 251);
            return b;
        }

        foreach (var len in new[] { 4, 8, 32, 60 })
        {
            var m = ValidFrame(len);
            byte[] packet = FedPacket.Pack(m);
            var msgs = FedPacket.Unpack(packet);
            Assert.Single(msgs);
            Assert.Equal(m, msgs[0]);
        }

        var batch = new byte[][] { ValidFrame(8), ValidFrame(32), ValidFrame(60) };
        byte[] bp = FedPacket.Pack((ReadOnlySpan<byte[]>)batch);
        var unpacked = FedPacket.Unpack(bp);
        Assert.Equal(batch.Length, unpacked.Length);
        for (int i = 0; i < batch.Length; i++) Assert.Equal(batch[i], unpacked[i]);
    }

    [Fact]
    public void Fast_and_original_pack_agree_on_a_real_broadcast_encode_shape()
    {
        // A real SingleShipUpdate-style single frame shape (the hot path message):
        // assert the two pack paths agree on the exact same non-empty payload bytes.
        var realistic = Msg(0x4E, 0x00, 0x1E, 0x00, 0x78, 0x00, 0x05, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00);
        Assert.Equal(FedPacket.Pack(new[] { realistic }), FedPacket.Pack(realistic));
    }
}
